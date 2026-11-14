import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { connect, operation, call, data } from './helpers.mjs';

async function fixture(t) {
  const runs = await mkdtemp(join(tmpdir(), 'corelmcp-extended-'));
  const session = await connect({ runs, fixture: true });
  t.after(() => session.close());
  return { ...session, runs };
}
test('preview selection and bounded recursive inspection reach the executor with defaults', async t => {
  const s = await fixture(t);
  const preview = await operation(s, 'corel_preview', { document_id: 'fixture', range: 'selection' });
  assert.equal(preview.status, 'succeeded');
  const inspected = await operation(s, 'corel_inspect_document', { document_id: 'fixture', recursive: true, include_text: true, text_limit: 20 });
  assert.equal(inspected.result.received.max_depth, 8);
  assert.equal(inspected.result.received.max_visited, 10000);
  assert.equal(inspected.result.received.text_limit, 20);
  const forbidden = await s.client.callTool({ name: 'corel_describe_api', arguments: { kind: 'members', name: 'doc.Delete()' } });
  assert.equal(forbidden.isError, true);
});
test('an applied operation with failed cleanup retains success and exposes MCP error on poll and retry', async t => {
  const s = await fixture(t);
  const args = { request_id: 'cleanup-failure', document_id: 'fixture', page_index: 1, static_ids: [1001], action: 'set_text', text: 'cleanup fail' };
  const job = await operation(s, 'corel_edit_text', args);
  assert.equal(job.status, 'succeeded');
  assert.equal(job.cleanup.completed, false);
  const response = await s.client.callTool({ name: 'corel_get_job', arguments: { job_id: job.job_id } });
  assert.equal(response.isError, true);
  const retry = await s.client.callTool({ name: 'corel_edit_text', arguments: args });
  assert.equal(retry.isError, true);
  assert.deepEqual(data(retry).result, { applied: true });
});
test('guides and resources are local and available while a COM job remains busy', async t => {
  const s = await fixture(t);
  await call(s, 'corel_edit_text', { request_id: 'busy-guides', document_id: 'fixture', page_index: 1, static_ids: [1001], action: 'set_text', text: 'delay' });
  const guide = await call(s, 'corel_guide', { topic: 'tools', offset: 0, limit: 200 });
  assert.match(guide.text, /corel_/);
  assert.equal(guide.text.length <= 200, true);
  const resources = await s.client.listResources();
  assert(resources.resources.some(r => r.uri === 'coreldraw://guide/tools'));
  const resource = await s.client.readResource({ uri: 'coreldraw://guide/tools' });
  assert.match(resource.contents[0].text, /corel_preview/);
  const note = await operation(s, 'corel_add_note', { request_id: 'local-note-busy', category: 'lesson', text: 'Локальная находка' });
  assert.equal(note.status, 'succeeded');
  assert.equal(note.result.verified, false);
  assert.match(await readFile(note.result.path, 'utf8'), /Локальная находка/);
});
test('export aliases reach the worker and incompatible options fail before dispatch', async t => {
  const s = await fixture(t);
  for (const format of ['jpeg','tif']) {
    const report = await operation(s, 'corel_export', { request_id: format, document_id: 'fixture', format, path: `C:\\Export.${format}` });
    assert.equal(report.result.received.format, format);
  }
  const invalid = await s.client.callTool({ name: 'corel_export', arguments: { request_id: 'bad-colors', document_id: 'fixture', format: 'pdf', path: 'C:\\Export.pdf', color_mode: 'cmyk' } });
  assert.equal(invalid.isError, true);
});
test('local diagnostic failures persist and repeats keep the original outcome', async t => {
  const s = await fixture(t);
  const source = await operation(s, 'corel_add_note', { request_id: 'source', category: 'idea', text: 'СЕКРЕТ_ЗАМЕТКИ' });
  const destination = join(s.runs, '..', `diagnostic-${Date.now()}.json`);
  const args = { request_id: 'diag', job_id: source.job_id, path: destination };
  const report = await operation(s, 'corel_export_diagnostics', args);
  assert.equal(report.status, 'succeeded');assert(!(await readFile(destination, 'utf8')).includes('СЕКРЕТ'));
  const conflict = await call(s, 'corel_export_diagnostics', { ...args, request_id: 'diag-exists' });
  assert.equal(conflict.status, 'failed');assert.equal(conflict.error.code, 'FILE_EXISTS');
  assert.equal((await call(s, 'corel_export_diagnostics', { ...args, request_id: 'diag-exists' })).job_id, conflict.job_id);
  const outside = join(s.runs, '..', 'external-log.txt');await writeFile(outside, 'EXTERNAL_SECRET');
  const saved = JSON.parse(await readFile(source.report_path, 'utf8'));saved.log_path=outside;await writeFile(source.report_path, JSON.stringify(saved));
  const polled=await call(s, 'corel_get_job', {job_id:source.job_id});assert.deepEqual(polled.log_tail,[]);assert(!JSON.stringify(polled.log_tail).includes('EXTERNAL_SECRET'));
});
