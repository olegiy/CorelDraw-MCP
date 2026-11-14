import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { randomUUID } from 'node:crypto';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { CSharpExecutor } from '../dist/executor.js';
import { JobManager } from '../dist/jobs.js';
import { MAX_WORKER_RECORD_BYTES } from '../dist/worker-protocol.js';

const ready = { type: 'ready', protocol_version: 1, worker_version: '0.4.0', runtime: '.NET 10.0.15', architecture: 'x64' };
const flush = () => new Promise(resolve => setImmediate(resolve));
function fixture(options = {}) {
  const child = Object.assign(new EventEmitter(), { stdin: new PassThrough(), stdout: new PassThrough(), stderr: new PassThrough(), pid: 123 });
  const writes = [];
  let launches = 0;
  child.stdin.on('data', value => writes.push(JSON.parse(value.toString())));
  const executor = new CSharpExecutor({ launchWorker: () => { launches++; return child; }, readyTimeoutMs: 1000, ...options });
  const emit = value => child.stdout.write(JSON.stringify(value) + '\n');
  const job = { job_id: randomUUID(), tool: 'corel_status', run_directory: 'C:\\CorelTest' };
  const run = (value = job, args = {}) => executor.run(value, args, () => {});
  const finish = (id = job.job_id, data = { ok: true }) => emit({ type: 'result', job_id: id, data });
  return { child, executor, writes, emit, job, run, finish, launches: () => launches };
}

test('C# executor dispatches exactly once only after a compatible ready handshake', async () => {
  const f = fixture();
  const pending = f.run();
  assert.equal(f.writes.length, 0);
  f.emit(ready);
  await flush();
  assert.deepEqual(f.writes, [{ job_id: f.job.job_id, tool: 'corel_status', args: {}, run_directory: 'C:\\CorelTest' }]);
  f.finish();
  assert.equal((await pending).ok, true);
  f.executor.close();
  assert.equal(f.child.stdin.writableEnded, true);
});

test('incompatible ready and exit before dispatch are proven nonexecution without fallback', async () => {
  for (const handshake of [{ ...ready, protocol_version: 2 }, { ...ready, architecture: 'x86' }, { ...ready, runtime: '.NET 8.0.33' }, { ...ready, extra: true }, null]) {
    const f = fixture();
    const pending = f.run();
    if (handshake) f.emit(handshake); else { f.child.emit('exit', 1); f.child.emit('close', 1); }
    const result = await pending;
    assert.equal(result.ok, false);
    assert.equal(result.error.applied, false);
    assert.equal(f.writes.length, 0);
    assert.equal(f.launches(), 1);
    f.executor.close();
  }
});

test('exit after dispatch has an unknown outcome and never restarts or replays', async () => {
  const f = fixture();
  const pending = f.run();
  const failed = assert.rejects(pending, /исход|результат/i);
  f.emit(ready); await flush();
  f.child.emit('exit', 1); f.child.emit('close', 1);
  await failed;
  const next = await f.run({ ...f.job, job_id: randomUUID() });
  assert.equal(next.error.applied, false);
  assert.equal(f.writes.length, 1);
  assert.equal(f.launches(), 1);
  f.executor.close();
});

test('noise, wrong IDs, duplicate keys and unknown fields cannot finish or release an operation', async () => {
  const f = fixture();
  const pending = f.run();
  f.emit(ready); await flush();
  f.child.stdout.write('PRIVATE_NOISE\n');
  f.finish(randomUUID());
  f.child.stdout.write(`{"type":"result","job_id":"${f.job.job_id}","data":{"ok":false,"ok":true}}\n`);
  f.emit({ type: 'result', job_id: f.job.job_id, data: { ok: true }, surprise: 1 });
  const busy = await f.run({ ...f.job, job_id: randomUUID() });
  assert.equal(busy.error.code, 'BUSY');
  assert.equal(f.writes.length, 1);
  f.finish();
  const result = await pending;
  assert.equal(result.ok, true);
  assert(result.warnings.length >= 3);
  assert(!JSON.stringify(result).includes('PRIVATE_NOISE'));
  f.executor.close();
});

test('fragmented UTF-8 and CRLF survive ready and result framing', async () => {
  const f = fixture();
  const pending = f.run();
  for (const byte of Buffer.from(JSON.stringify(ready) + '\r\n')) f.child.stdout.write(Buffer.from([byte]));
  await flush();
  const result = { type: 'result', job_id: f.job.job_id, data: { ok: true, result: { text: 'Ёж 🦔' } } };
  for (const byte of Buffer.from(JSON.stringify(result) + '\r\n')) f.child.stdout.write(Buffer.from([byte]));
  assert.equal((await pending).result.text, 'Ёж 🦔');
  f.executor.close();
});

test('oversized stdout and EOF preserve unknown outcomes without replay', async () => {
  const f = fixture();
  const pending = f.run();
  const failed = assert.rejects(pending, /исход|результат/i);
  f.emit(ready); await flush();
  f.child.stdout.write(Buffer.alloc(MAX_WORKER_RECORD_BYTES + 50, 0x20));
  assert.equal((await f.run()).error.code, 'BUSY');
  f.child.stdout.end();
  await failed;
  assert.equal(f.writes.length, 1);
  f.executor.close();
});

test('startup timeout and invalid requests fail before dispatch and do not launch another backend', async () => {
  const f = fixture({ readyTimeoutMs: 15 });
  const result = await f.run();
  assert.equal(result.error.applied, false);
  assert.equal(f.writes.length, 0);
  assert.equal(f.child.stdin.writableEnded, true);
  assert.equal(f.launches(), 1);
  const invalid = fixture();
  for (const args of [{ x: Infinity }, { x: 'a'.repeat(MAX_WORKER_RECORD_BYTES) }])
    assert.equal((await invalid.run(invalid.job, args)).error.applied, false);
  assert.equal((await invalid.run({ ...invalid.job, job_id: 'not-a-job' })).error.applied, false);
  assert.equal((await invalid.run({ ...invalid.job, run_directory: 'relative' })).error.applied, false);
  assert.equal(invalid.launches(), 0);
  f.executor.close(); invalid.executor.close();
});

test('close drains an accepted operation including cleanup before ending stdin', async () => {
  const f = fixture();
  const pending = f.run();
  f.emit(ready); await flush();
  f.executor.close();
  assert.equal(f.child.stdin.writableEnded, false);
  assert.equal((await f.run({ ...f.job, job_id: randomUUID() })).error.applied, false);
  f.finish(); await pending;
  assert.equal(f.child.stdin.writableEnded, true);
});

test('C# measurement uses the same bounded TS geometry postprocessing and artifact checks', async () => {
  const f = fixture();
  const directory = mkdtempSync(join(tmpdir(), 'corelmcp-csharp-measure-'));
  const path = join(directory, 'geometry.json');
  writeFileSync(path, JSON.stringify({ objects: [{ static_id: 7, segments: [{ kind: 'line', p0: { x: 0, y: 0 }, p1: { x: 20, y: 0 } }, { kind: 'line', p0: { x: 20, y: 0 }, p1: { x: 20, y: 10 } }, { kind: 'line', p0: { x: 20, y: 10 }, p1: { x: 0, y: 10 } }] }] }));
  const job = { ...f.job, tool: 'corel_measure_shapes', run_directory: directory };
  const pending = f.run(job, { tolerance_mm: .01 });
  f.emit(ready); await flush();
  f.finish(job.job_id, { ok: true, result: { snapshot_path: path, original_state_preserved: true }, artifacts: [path] });
  const result = await pending;
  assert.equal(result.ok, true);
  assert.equal(result.result.objects[0].rectangle.width_mm, 20);
  assert.equal(result.result.original_state_preserved, true);
  const outside = f.run({ ...job, job_id: randomUUID(), run_directory: join(directory, 'nested') });
  await flush();
  f.finish(f.writes.at(-1).job_id, { ok: true, result: { snapshot_path: path }, artifacts: [path] });
  assert.equal((await outside).error.code, 'GEOMETRY_INVALID_PATH');
  f.executor.close();
});

test('C# jobs keep the slot after deadline and accept a late result without replay', async () => {
  const f = fixture();
  const jobs = new JobManager(mkdtempSync(join(tmpdir(), 'corelmcp-csharp-job-')), f.executor, 20);
  const job = jobs.submit('corel_status', { request_id: 'late-csharp' });
  await flush(); f.emit(ready); await flush();
  await new Promise(resolve => setTimeout(resolve, 35));
  assert.equal(jobs.get(job.job_id).status, 'timed_out');
  assert.throws(() => jobs.submit('corel_status', {}), /занят/);
  f.finish(job.job_id, { ok: true, result: { late: true } });
  await flush();
  assert.equal(jobs.get(job.job_id).status, 'succeeded');
  assert.equal(jobs.get(job.job_id).deadline_exceeded, true);
  assert.equal(jobs.submit('corel_status', { request_id: 'late-csharp' }).job_id, job.job_id);
  assert.equal(f.writes.length, 1);
  jobs.close();
});

test('C# job reports distinguish startup nonexecution from worker loss after dispatch', async () => {
  for (const dispatched of [false, true]) {
    const f = fixture();
    const jobs = new JobManager(mkdtempSync(join(tmpdir(), 'corelmcp-csharp-loss-')), f.executor);
    const job = jobs.submit('corel_status', { request_id: 'single-request' });
    await flush();
    if (dispatched) { f.emit(ready); await flush(); }
    f.child.emit('exit', 1); f.child.emit('close', 1); await flush();
    const report = jobs.get(job.job_id);
    assert.equal(report.status, dispatched ? 'unknown' : 'failed');
    assert.equal(jobs.submit('corel_status', { request_id: 'single-request' }).status, report.status);
    assert.equal(f.writes.length, dispatched ? 1 : 0);
    assert.equal(f.launches(), 1);
    jobs.close();
  }
});
