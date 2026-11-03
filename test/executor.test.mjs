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
  const executor = new PowerShellExecutor('CorelDRAW.Application.27', () => child);
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
