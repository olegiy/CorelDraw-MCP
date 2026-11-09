import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { PowerShellExecutor } from '../dist/executor.js';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

function fixture() {
  const child = Object.assign(new EventEmitter(), { stdin: new PassThrough(), stdout: new PassThrough(), stderr: new PassThrough(), pid: 1 });
  const writes = [];
  child.stdin.on('data', value => writes.push(JSON.parse(value.toString())));
  const executor = new PowerShellExecutor('CorelDRAW.Application.27', () => child, 'off');
  const emit = message => child.stdout.write(JSON.stringify(message) + '\n');
  const run = id => executor.run({ job_id: id, tool: 'corel_execute_vba', run_directory: 'C:\\Test' }, {}, () => {});
  return { child, executor, writes, emit, run };
}
test('malformed worker output does not release an in-flight operation', { skip: process.platform !== 'win32' }, async () => {
  const { child, executor, emit, run } = fixture();
  const pending = run('first');
  child.stdout.write('not a protocol message\n');
  await assert.rejects(run('second'), /уже занят/);
  emit({ type: 'result', job_id: 'first', data: { ok: true, result: { done: true } } });
  const result = await pending;
  assert.equal(result.ok, true);
  assert(result.warnings.some(value => value.includes('Некорректный JSON')));
  const next = run('second');
  emit({ type: 'result', job_id: 'second', data: { ok: true } });
  await next;
  executor.close(); child.emit('exit', 0);
});

test('observer handshake waits for arm/disarm and never resolves a blocked worker or releases its slot', { skip: process.platform !== 'win32' }, async () => {
  const child = Object.assign(new EventEmitter(), { stdin: new PassThrough(), stdout: new PassThrough(), stderr: new PassThrough(), pid: 1 });
  const writes = [];
  child.stdin.on('data', value => writes.push(JSON.parse(value.toString())));
  let armed, disarmed, publish;
  const summary = { schema_version: 1, mode: 'observe', state: 'observing', detected: 0, action_attempted: 0, dismissed: 0, requires_operator: 0, events_truncated: false };
  const observer = { summary, start: async () => true, arm: () => new Promise(resolve => { armed = resolve; }),
    disarm: () => new Promise(resolve => { disarmed = resolve; }), stop: async () => { summary.state = 'stopped'; } };
  const executor = new PowerShellExecutor('CorelDRAW.Application.27', () => child, 'observe', (_job, _pid, update) => { publish = update; return observer; });
  const updates = [];
  const pending = executor.run({ job_id: 'observer-job', tool: 'corel_execute_vba', run_directory: 'C:\\Test' }, {}, () => {}, value => updates.push(value));
  await new Promise(resolve => setTimeout(resolve, 0));
  const emit = value => child.stdout.write(JSON.stringify({ job_id: 'observer-job', ...value }) + '\n');
  emit({ type: 'observer_prepare', handshake_id: 'prepare1', phase: 'compiling', project_name: 'CorelMcp_test', app_hwnd: '42' });
  assert.equal(writes.filter(value => value.type === 'observer_ready').length, 0);
  armed({ available: true, compile_error_confirmed: false });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(writes.at(-1).type, 'observer_ready');
  publish({ ...summary, detected: 1, requires_operator: 1 });
  assert.equal(updates.at(-1).detected, 1);
  await assert.rejects(executor.run({ job_id: 'next' }, {}, () => {}), /уже занят/);
  emit({ type: 'observer_finish', handshake_id: 'finish1' });
  disarmed({ available: true, compile_error_confirmed: false });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(writes.at(-1).type, 'observer_finished');
  emit({ type: 'result', data: { ok: true } });
  assert.equal((await pending).ok, true);
  executor.close(); child.emit('exit', 0);
});

test('observer startup failure cannot release the COM slot, leak an error or replay the worker request', { skip: process.platform !== 'win32' }, async () => {
  const child = Object.assign(new EventEmitter(), { stdin: new PassThrough(), stdout: new PassThrough(), stderr: new PassThrough(), pid: 1 });
  const writes = [], updates = [];
  child.stdin.on('data', value => writes.push(JSON.parse(value.toString())));
  const executor = new PowerShellExecutor('CorelDRAW.Application.27', () => child, 'observe', () => { throw new Error('PRIVATE_CANARY'); });
  const pending = executor.run({ job_id: 'failure', tool: 'corel_execute_vba', run_directory: 'C:\\Test' }, {}, () => {}, summary => updates.push(summary));
  await assert.rejects(executor.run({ job_id: 'next' }, {}, () => {}), /уже занят/);
  assert.equal(writes.filter(value => value.job_id === 'failure').length, 1);
  assert.equal(updates.at(-1).state, 'degraded');
  child.stdout.write(JSON.stringify({ type: 'result', job_id: 'failure', data: { ok: true } }) + '\n');
  const result = await pending;
  assert.equal(result.ok, true);
  assert(!JSON.stringify(result).includes('PRIVATE_CANARY'));
  executor.close(); child.emit('exit', 0);
});
test('VBA JSON is validated before cleanup and preserves arrays and null', { skip: process.platform !== 'win32' }, async () => {
  const { child, executor, writes, emit, run } = fixture();
  for (const [index, json] of ['[]', '[1]', 'null', ' ', '{"x":true,}'].entries()) {
    const id = String(index);
    const pending = run(id);
    emit({ type: 'validate_json', job_id: id, json });
    const valid = index < 3;
    assert.deepEqual(writes.at(-1), { type: 'json_validation', job_id: id, ok: valid });
    emit({ type: 'result', job_id: id, data: valid ? { ok: true, result: { value_json: json } } : { ok: false, error: { code: 'INVALID_VBA_JSON', stage: 'validating_result', message: 'Expected JSON' }, cleanup: { project_retained: true } } });
    const result = await pending;
    assert.equal(result.ok, valid);
    if (valid) assert.deepEqual(result.result.value, JSON.parse(json));
    else assert.equal(result.cleanup.project_retained, true);
  }
  executor.close(); child.emit('exit', 0);
});
test('measurement postprocessing checks artifact path and parses bounded vector snapshot', { skip: process.platform !== 'win32' }, async () => {
  const { child, executor, emit } = fixture();
  const directory = mkdtempSync(join(tmpdir(), 'corelmcp-measure-'));
  const path = join(directory, 'geometry.json');
  writeFileSync(path, JSON.stringify({ objects: [{ static_id: 7, segments: [{ kind: 'line', p0: { x: 0, y: 0 }, p1: { x: 20, y: 0 } }, { kind: 'line', p0: { x: 20, y: 0 }, p1: { x: 20, y: 10 } }, { kind: 'line', p0: { x: 20, y: 10 }, p1: { x: 0, y: 10 } }] }] }));
  const pending = executor.run({ job_id: 'measure', tool: 'corel_measure_shapes', run_directory: directory }, { tolerance_mm: .01 }, () => {});
  emit({ type: 'result', job_id: 'measure', data: { ok: true, result: { snapshot_path: path }, artifacts: [path], cleanup: { completed: true } } });
  const result = await pending;
  assert.equal(result.ok, true); assert.equal(result.result.objects[0].rectangle.width_mm, 20); assert.equal(result.result.aggregate.min_width_mm, 10);
  const invalid = executor.run({ job_id: 'outside', tool: 'corel_measure_shapes', run_directory: join(directory, 'nested') }, {}, () => {});
  emit({ type: 'result', job_id: 'outside', data: { ok: true, result: { snapshot_path: path }, artifacts: [path] } });
  assert.equal((await invalid).error.code, 'GEOMETRY_INVALID_PATH');
  executor.close(); child.emit('exit', 0);
});
