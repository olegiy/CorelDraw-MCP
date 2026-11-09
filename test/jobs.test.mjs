import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { JobManager } from '../dist/jobs.js';

test('observer snapshots persist during timeout without releasing COM or erasing counters on result', async () => {
  let finish, observerUpdate;
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-observer-job-'));
  const executor = { run: (_job, _args, _progress, observe) => new Promise(resolve => { finish = resolve; observerUpdate = observe; }), close() {} };
  const jobs = new JobManager(root, executor, 20);
  const job = jobs.submit('synthetic', { request_id: 'observer-once' });
  await Promise.resolve();
  await new Promise(resolve => setTimeout(resolve, 30));
  const summary = { schema_version: 1, mode: 'observe', state: 'observing', detected: 2, requires_operator: 2,
    action_attempted: 0, dismissed: 0, events_truncated: true, text: 'PRIVATE_CANARY' };
  observerUpdate(summary);
  assert.equal(jobs.get(job.job_id).vba_observer.detected, 2);
  assert.equal(jobs.get(job.job_id).status, 'timed_out');
  assert.throws(() => jobs.submit('synthetic', {}), /занят/);
  assert(!readFileSync(job.report_path, 'utf8').includes('PRIVATE_CANARY'));
  finish({ ok: true, vba_observer: { ...summary, detected: 0, requires_operator: 0, events_truncated: false, state: 'stopped' } });
  await new Promise(resolve => setTimeout(resolve, 20));
  observerUpdate({ ...summary, detected: 99 });
  const completed = jobs.get(job.job_id);
  assert.equal(completed.status, 'succeeded');
  assert.equal(completed.deadline_exceeded, true);
  assert.equal(completed.vba_observer.detected, 2);
  assert.equal(completed.vba_observer.events_truncated, true);
  assert.equal(jobs.submit('synthetic', { request_id: 'observer-once' }).job_id, job.job_id);
  jobs.close();
});

test('transport close lets an in-flight mutation finish and writes its report', async () => {
  let finish;
  let closed = false;
  const executor = { run: () => new Promise(resolve => { finish = resolve; }), close: () => { closed = true; } };
  const jobs = new JobManager(mkdtempSync(join(tmpdir(), 'corelmcp-jobs-')), executor);
  const job = jobs.submit('synthetic', { request_id: 'pending' });
  await Promise.resolve();
  jobs.close();
  assert.equal(closed, false);
  assert.throws(() => jobs.submit('synthetic', {}), /завершает работу/);
  finish({ ok: true, result: { changed: true } });
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal(closed, true);
  const saved = JSON.parse(readFileSync(job.report_path, 'utf8'));
  assert.equal(saved.status, 'succeeded');
});
test('an orphaned persisted request is reported unknown and never re-executed', async () => {
  let executions = 0;
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-orphan-'));
  const jobs = new JobManager(root, { run: async () => { executions++; return { ok: true }; }, close() {} });
  const job = jobs.submit('synthetic', { request_id: 'once' });
  await jobs.wait(job.job_id, 1);
  const saved = JSON.parse(readFileSync(job.report_path, 'utf8'));
  saved.owner_pid = 2147483647; saved.owner_session = 'previous'; saved.status = 'running';
  writeFileSync(job.report_path, JSON.stringify(saved));
  const repeated = jobs.submit('synthetic', { request_id: 'once' });
  assert.equal(repeated.status, 'unknown');
  assert.equal(repeated.error.code, 'OWNER_LOST');
  assert.equal(executions, 1);
  jobs.close();
});
test('report and log failures keep the in-memory outcome and release the busy slot', async () => {
  let finish, progress, observe;
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-io-'));
  const executor = { run: (_job, _args, update, observerUpdate) => new Promise(resolve => { finish = resolve; progress = update; observe = observerUpdate; }), close() {} };
  const jobs = new JobManager(root, executor);
  const job = jobs.submit('synthetic', { request_id: 'io-once' });
  await Promise.resolve();
  // Simulate storage becoming unwritable after dispatch without changing ACLs.
  for (const path of [job.log_path, job.report_path]) { rmSync(path); mkdirSync(path); }
  progress('executing');
  observe({ schema_version: 1, mode: 'observe', state: 'observing', detected: 1, requires_operator: 1,
    action_attempted: 0, dismissed: 0, events_truncated: false });
  assert.equal(jobs.get(job.job_id).stage, 'executing');
  assert.equal(jobs.get(job.job_id).vba_observer.detected, 1);
  assert.throws(() => jobs.submit('synthetic', {}), /занят заданием/);
  finish({ ok: true, result: { changed: true } });
  await new Promise(resolve => setTimeout(resolve, 20));
  const completed = jobs.get(job.job_id);
  assert.equal(completed.status, 'succeeded');
  assert(completed.warnings.some(warning => warning.includes('сохранить отчёт')));
  assert.equal(jobs.submit('synthetic', { request_id: 'io-once' }).job_id, job.job_id);
  const next = jobs.submit('synthetic', {});
  await Promise.resolve(); finish({ ok: true });
  await jobs.wait(next.job_id, 1);
  jobs.close();
});
