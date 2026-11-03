import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { connect, operation, call, data } from './helpers.mjs';

async function fixture(t) {
  const runs = await mkdtemp(join(tmpdir(), 'corelmcp-protocol-'));
  const session = await connect({ fixture: true, runs });
  t.after(() => session.close());
  return { ...session, runs };
}
test('stdio negotiation publishes all tools and strict schemas', async t => {
  const session = await fixture(t);
  const list = await session.client.listTools();
  for (const name of ['corel_measure_shapes','corel_describe_api','corel_add_note','corel_export_diagnostics','corel_prune_artifacts','corel_list_artifacts','corel_guide']) assert(list.tools.some(tool => tool.name === name));
  assert(list.tools.every(tool => tool.inputSchema.type === 'object'));
  const execution = list.tools.find(tool => tool.name === 'corel_execute_vba');
  assert.equal(execution.annotations.destructiveHint, true);
  assert.equal(execution.inputSchema.additionalProperties, false);
  assert(execution.inputSchema.required.includes('request_id'));
  assert.match(execution.description, /sandbox/);
});
test('invalid inputs fail before any job is created', async t => {
  const session = await fixture(t);
  for (const args of [{ request_id: 'bad', width_mm: -1 }, { width_mm: 1 }, { request_id: 'bad', surprise: true }]) {
    const result = await session.client.callTool({ name: 'corel_create_document', arguments: args });
    assert.equal(result.isError, true);
  }
  const traversal = await call(session, 'corel_get_job', { job_id: '..\\request.json' });
  assert.equal(traversal.error.code, 'INVALID_JOB_ID');
});
test('identical requests reuse persisted jobs; changed arguments conflict', async t => {
  const session = await fixture(t);
  const args = { request_id: 'one request', width_mm: 123 };
  const first = await operation(session, 'corel_create_document', args);
  const repeated = await call(session, 'corel_create_document', args);
  assert.equal(repeated.job_id, first.job_id);
  assert.equal(repeated.status, 'succeeded');
  const changed = await call(session, 'corel_create_document', { ...args, width_mm: 124 });
  assert.equal(changed.error.code, 'REQUEST_ID_CONFLICT');
  const saved = JSON.parse(await readFile(first.report_path, 'utf8'));
  assert.equal(saved.result.received.width_mm, 123);
  assert(first.log_tail.some(line => line.includes('Отправлено:')));
  assert(first.log_tail.some(line => line.includes('Завершено: succeeded')));
  const lastLine = await call(session, 'corel_get_job', { job_id: first.job_id, log_lines: 1 });
  assert.equal(lastLine.log_tail.length, 1); assert.equal(lastLine.log_truncated, true);
  const other = await connect({ fixture: true, runs: session.runs });
  try { assert.equal((await call(other, 'corel_create_document', args)).job_id, first.job_id); } finally { await other.close(); }
});
test('Unicode, Windows paths and literal shell metacharacters round trip', async t => {
  const session = await fixture(t);
  const code = 'Привет "$()" `backticks` C:\\Макеты с пробелами\\тест.cdr';
  const job = await operation(session, 'corel_execute_vba', { request_id: 'юникод', document_id: 'synthetic', code });
  assert.equal(job.result.received.code, code);
});
test('deadline preserves busy state and late completion reconciles the result', async t => {
  const session = await fixture(t);
  const args = { request_id: 'slow', document_id: 'synthetic', code: 'delay', deadline_seconds: 1 };
  const first = await call(session, 'corel_execute_vba', args);
  const timed = await call(session, 'corel_get_job', { job_id: first.job_id, wait_seconds: 2 });
  assert.equal(timed.status, 'timed_out');
  assert.equal(timed.deadline_exceeded, true);
  const busy = await call(session, 'corel_status');
  assert.equal(busy.error.code, 'BUSY');
  assert.equal((await call(session, 'corel_execute_vba', args)).job_id, first.job_id);
  await new Promise(resolve => setTimeout(resolve, 600));
  const complete = await call(session, 'corel_get_job', { job_id: first.job_id });
  assert.equal(complete.status, 'succeeded');
  assert.equal(complete.deadline_exceeded, true);
  assert(complete.finished_at);
});
test('worker errors use MCP isError and retain structured diagnostics', async t => {
  const session = await fixture(t);
  const report = await operation(session, 'corel_execute_vba', { request_id: 'failure', document_id: 'synthetic', code: 'fail' });
  const result = await session.client.callTool({ name: 'corel_get_job', arguments: { job_id: report.job_id } });
  assert.equal(result.isError, true);
  assert.equal(data(result).error.number, 123);
  const unknown = await operation(session, 'corel_execute_vba', { request_id: 'unknown', document_id: 'synthetic', code: 'lose executor' });
  assert.equal(unknown.status, 'unknown');
  assert.equal(unknown.error.code, 'EXECUTOR_LOST');
});
test('completed preview includes an MCP PNG image only when requested', async t => {
  const session = await fixture(t);
  const report = await operation(session, 'corel_preview', { document_id: 'synthetic' });
  const image = await session.client.callTool({ name: 'corel_get_job', arguments: { job_id: report.job_id, include_preview: true } });
  assert.equal(image.content[1].type, 'image');
  assert.equal(image.content[1].mimeType, 'image/png');
  const plain = await session.client.callTool({ name: 'corel_get_job', arguments: { job_id: report.job_id } });
  assert.equal(plain.content.length, 1);
});
