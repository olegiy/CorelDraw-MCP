import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { PowerShellExecutor } from '../dist/executor.js';

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
  await assert.rejects(run('second'), /already busy/);
  emit({ type: 'result', job_id: 'first', data: { ok: true, result: { done: true } } });
  const result = await pending;
  assert.equal(result.ok, true);
  assert(result.warnings.some(value => value.includes('malformed')));
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
