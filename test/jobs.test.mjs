import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { JobManager } from '../dist/jobs.js';

test('native indeterminate outcome remains unknown with diagnostics and is never replayed', async t => {
  let calls=0;
  const jobs=new JobManager(mkdtempSync(join(tmpdir(),'corelmcp-native-unknown-')),{
    run:async()=>{calls++;return {ok:false,error:{code:'NATIVE_OUTCOME_UNKNOWN',stage:'native_applying',message:'Исход не подтверждён'},result:{partial:true,created_static_ids:[4]},cleanup:{completed:true}};},close(){}
  });
  t.after(()=>jobs.close());
  const first=jobs.submit('corel_create_batch',{request_id:'native-once'});
  await jobs.wait(first.job_id,1);
  const report=jobs.get(first.job_id);
  assert.equal(report.status,'unknown');assert.equal(report.error.code,'NATIVE_OUTCOME_UNKNOWN');
  assert.deepEqual(report.result.created_static_ids,[4]);
  assert.equal(jobs.submit('corel_create_batch',{request_id:'native-once'}).job_id,first.job_id);
  assert.equal(calls,1);
});

test('timeout preserves the slot and a late completion keeps the original request identity', async t => {
  let finish;
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-late-job-'));
  const executor = { run: () => new Promise(resolve => { finish = resolve; }), close() {} };
  const jobs = new JobManager(root, executor, 20);
  t.after(() => { finish?.({ ok: true }); jobs.close(); });
  const job = jobs.submit('synthetic', { request_id: 'once' });
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.equal(jobs.get(job.job_id).status, 'timed_out');
  assert.throws(() => jobs.submit('synthetic', {}), /занят/);
  finish({ ok: true, result: { observed: true } });
  await new Promise(resolve => setImmediate(resolve));
  await jobs.wait(job.job_id, 1);
  const completed = jobs.get(job.job_id);
  assert.equal(completed.status, 'succeeded'); assert.equal(completed.deadline_exceeded, true);
  assert.equal(jobs.submit('synthetic', { request_id: 'once' }).job_id, job.job_id);
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
  let finish, progress;
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-io-'));
  const executor = { run: (_job, _args, update) => new Promise(resolve => { finish = resolve; progress = update; }), close() {} };
  const jobs = new JobManager(root, executor);
  const job = jobs.submit('synthetic', { request_id: 'io-once' });
  await Promise.resolve();
  // Simulate storage becoming unwritable after dispatch without changing ACLs.
  for (const path of [job.log_path, job.report_path]) { rmSync(path); mkdirSync(path); }
  progress('executing');
  assert.equal(jobs.get(job.job_id).stage, 'executing');
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
