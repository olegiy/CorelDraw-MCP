import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { JobManager } from '../dist/jobs.js';
import { spawn } from 'node:child_process';

const tick = () => new Promise(resolve => setTimeout(resolve, 20));

test('observer summary survives prune and diagnostic export while raw journal and paths remain local', async () => {
  const { ArtifactStore, diagnosticSummary } = await import('../dist/artifacts.js');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-observer-prune-'));
  const jobs = new JobManager(root, { async run(job) {
    const artifact = join(job.run_directory, 'vba-observer.jsonl');
    writeFileSync(artifact, 'PRIVATE_WINDOW_CANARY');
    return { ok: true, cleanup: { completed: true }, vba_observer: { schema_version: 1, mode: 'observe', state: 'stopped',
      detected: 3, requires_operator: 3, action_attempted: 0, dismissed: 0, events_truncated: false, artifact, raw: 'PRIVATE_WINDOW_CANARY' } };
  }, close() {} });
  const job = jobs.submit('fixture', { request_id: 'observer-prune' }); await tick();
  const exported = diagnosticSummary(jobs.get(job.job_id));
  assert.equal(exported.vba_observer.detected, 3);
  assert(!JSON.stringify(exported).includes('artifact'));
  assert(!JSON.stringify(exported).includes('PRIVATE_WINDOW_CANARY'));
  const store = new ArtifactStore(root, id => jobs.get(id));
  const listed = store.list().items[0];
  assert.equal(store.prune([{ job_id: job.job_id, prune_token: listed.prune_token }]).items[0].pruned, true);
  const compact = jobs.get(job.job_id);
  assert.equal(compact.vba_observer.detected, 3);
  assert.equal(compact.vba_observer.artifact, undefined);
  assert.equal(existsSync(join(job.run_directory, 'vba-observer.jsonl')), false);
});
test('artifact pruning removes internal data, retains external files and prevents mutation replay after restart', async () => {
  const { ArtifactStore } = await import('../dist/artifacts.js');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-prune-'));
  let executions = 0;
  const executor = { async run() { executions++; return { ok: true, result: { value: 'secret' }, cleanup: { completed: true } }; }, close() {} };
  const jobs = new JobManager(root, executor);
  const args = { request_id: 'once', code: 'secret' };
  const accepted = jobs.submit('mutation', args); await tick();
  const external = join(root, '..', `external-${Date.now()}.cdr`); writeFileSync(external, 'USER DRAWING');
  writeFileSync(join(accepted.run_directory, 'big.gms'), 'PRIVATE SOURCE');
  const store = new ArtifactStore(root, id => jobs.get(id));
  const listed = store.list(0, 50).items.find(x => x.job_id === accepted.job_id);
  assert.equal(listed.protected, false);
  const result = store.prune([{ job_id: listed.job_id, prune_token: listed.prune_token }]);
  assert.equal(result.items[0].pruned, true);
  assert.equal(existsSync(join(accepted.run_directory, 'big.gms')), false);
  assert.equal(existsSync(join(accepted.run_directory, 'request.json')), false);
  assert.equal(readFileSync(external, 'utf8'), 'USER DRAWING');
  const restarted = new JobManager(root, executor);
  const repeated = restarted.submit('mutation', args);
  assert.equal(repeated.details_pruned, true);
  assert.equal(repeated.status, 'succeeded');
  await tick(); assert.equal(executions, 1);
  assert.throws(() => restarted.submit('mutation', { ...args, code: 'different' }), e => e.code === 'REQUEST_ID_CONFLICT');
});
test('prune token rejects changed content, protects unknown and refuses directory links', async () => {
  const { ArtifactStore } = await import('../dist/artifacts.js');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-protect-'));
  const jobs = new JobManager(root, { async run() { return { ok: true, cleanup: { completed: true } }; }, close() {} });
  const job = jobs.submit('fixture', { request_id: 'change' }); await tick();
  const store = new ArtifactStore(root, id => jobs.get(id));
  const first = store.list(0, 50).items[0];
  writeFileSync(join(job.run_directory, 'changed.txt'), 'new');
  const refused = store.prune([{ job_id: job.job_id, prune_token: first.prune_token }]);
  assert.equal(refused.items[0].code, 'ARTIFACTS_CHANGED');
  const outside = mkdtempSync(join(tmpdir(), 'corelmcp-linktarget-'));
  writeFileSync(join(outside, 'keep.txt'), 'KEEP');
  symlinkSync(outside, join(job.run_directory, 'linked'), process.platform === 'win32' ? 'junction' : 'dir');
  const linked = store.list(0, 50).items[0];
  assert(linked.protection_reasons.includes('REPARSE_POINT'));
  assert.equal(store.prune([{ job_id: job.job_id, prune_token: linked.prune_token }]).items[0].pruned, false);
  assert.equal(readFileSync(join(outside, 'keep.txt'), 'utf8'), 'KEEP');
  const report = JSON.parse(readFileSync(job.report_path)); report.status = 'unknown'; writeFileSync(job.report_path, JSON.stringify(report));
  assert(store.list(0, 50).items[0].protection_reasons.includes('UNKNOWN_OUTCOME'));
});
test('diagnostic allowlist excludes source paths results request IDs and error messages', async () => {
  const { diagnosticSummary } = await import('../dist/artifacts.js');
  const secret = 'SECRET C:\\Private\\Ёж.cdr';
  const result = diagnosticSummary({ job_id: 'req-' + 'a'.repeat(64), tool: 'corel_execute_vba', status: 'failed', stage: 'executing_vba', started_at: '2026-11-03T00:00:00.000Z', request_id: secret, result: secret, run_directory: secret, error: { code: 'VBA_RUNTIME_ERROR', number: 555, message: secret, source: secret }, cleanup: { completed: false, errors: [{ step: 'settings', code: 'CLEANUP_FAILED', message: secret }] }, versions: { server: '0.2.0', node: '24.18.1' } });
  assert.equal(JSON.stringify(result).includes('SECRET'), false);
  assert.equal(result.error.code, 'VBA_RUNTIME_ERROR');
  assert.equal(result.error.number, 555);
  assert.equal(result.versions.coreldraw, 'нет данных');
  assert.equal(result.cleanup.errors[0].code, 'CLEANUP_FAILED');
});
test('two independent cleaners serialize one candidate and keep its durable request identity', async () => {
  const { ArtifactStore } = await import('../dist/artifacts.js');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-two-cleaners-'));
  const jobs = new JobManager(root, { async run() { return { ok: true, cleanup: { completed: true } }; }, close() {} });
  const job = jobs.submit('fixture', { request_id: 'two-cleaners' });await tick();
  writeFileSync(join(job.run_directory,'large.bin'),Buffer.alloc(1024*1024,42));
  const candidate=new ArtifactStore(root,id=>jobs.get(id)).list().items[0];
  const script=`import{ArtifactStore}from ${JSON.stringify(new URL('../dist/artifacts.js',import.meta.url).href)};import{readFileSync}from'node:fs';import{join}from'node:path';const[root,id,token]=process.argv.slice(1);const store=new ArtifactStore(root,id=>JSON.parse(readFileSync(join(root,id,'report.json'),'utf8')));console.log(JSON.stringify(store.prune([{job_id:id,prune_token:token}])));`;
  const run=()=>new Promise((resolve,reject)=>{const child=spawn(process.execPath,['--input-type=module','-e',script,root,candidate.job_id,candidate.prune_token],{windowsHide:true});let out='',error='';child.stdout.on('data',b=>out+=b);child.stderr.on('data',b=>error+=b);child.on('error',reject);child.on('exit',code=>code===0?resolve(JSON.parse(out).items[0]):reject(new Error(error)));});
  const results=await Promise.all([run(),run()]);assert.equal(results.filter(result=>result.pruned).length,1);
  const saved=jobs.get(job.job_id);assert.equal(saved.details_pruned,true);assert.equal(saved.pruned_inventory.token,candidate.prune_token);
  assert.equal(jobs.submit('fixture',{request_id:'two-cleaners'}).job_id,job.job_id);
});

test('all unsafe outcomes remain protected and policy/applied metadata survives eligible prune', async () => {
  const { ArtifactStore } = await import('../dist/artifacts.js');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-protections-'));
  const jobs = new JobManager(root, { async run() { return { ok: true, cleanup: { completed: true } }; }, close() {} });
  const job = jobs.submit('corel_status', {}); await tick();
  const original = JSON.parse(readFileSync(job.report_path));
  const store = new ArtifactStore(root, id => jobs.get(id));
  for (const changes of [
    { status: 'running' }, { status: 'timed_out' }, { status: 'unknown' },
    { cleanup: { completed: false } }, { cleanup: { completed: true, project_retained: true } },
  ]) {
    writeFileSync(job.report_path, JSON.stringify({ ...original, ...changes }));
    const listed = store.list().items[0];
    assert.equal(listed.protected, true);
    assert.equal(store.prune([{ job_id: job.job_id, prune_token: listed.prune_token }]).items[0].pruned, false);
    assert(existsSync(join(job.run_directory, 'request.json')));
  }
  writeFileSync(job.report_path, JSON.stringify({ ...original, status: 'failed', error: { code: 'RECIPE_ACTION_FAILED', applied: true }, tool_policy: { source: 'user', effective_count: 17, registered_count: 17, disabled_count: 1, total_count: 18, config_path: 'private' } }));
  const listed = store.list().items[0];
  assert.equal(store.prune([{ job_id: job.job_id, prune_token: listed.prune_token }]).items[0].pruned, true);
  const compact = jobs.get(job.job_id);
  assert.equal(compact.error.applied, true);
  assert.equal(compact.tool_policy.effective_count, 17);
});

test('diagnostic default destination is unique and redacted without a COM call', async () => {
  const { LocalOperations } = await import('../dist/local-operations.js');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-diag-'));
  const jobs = new JobManager(join(root, 'runs'), { async run() { throw new Error('COM must not run'); }, close() {} });
  const local = new LocalOperations(jobs);
  const source = jobs.submitLocal('corel_add_note', { request_id: 'source' }, () => ({ result: { private: 'secret' } }));
  const first = jobs.submitLocal('corel_export_diagnostics', { request_id: 'first', job_id: source.job_id }, job => local.run('corel_export_diagnostics', { job_id: source.job_id }, job));
  const second = jobs.submitLocal('corel_export_diagnostics', { request_id: 'second', job_id: source.job_id }, job => local.run('corel_export_diagnostics', { job_id: source.job_id }, job));
  assert.equal(first.status, 'succeeded', JSON.stringify(first));
  assert.equal(second.status, 'succeeded');
  assert.notEqual(first.result.path, second.result.path);
  assert(!readFileSync(first.result.path, 'utf8').includes('secret'));
});
