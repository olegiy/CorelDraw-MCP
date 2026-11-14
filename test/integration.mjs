import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { call, connect, operation } from './helpers.mjs';
if (process.env.CORELDRAW_WORKER_LIVE !== '1') throw new Error('Для живых тестов нужен CORELDRAW_WORKER_LIVE=1.');
const directory = fileURLToPath(new URL(`../artifacts/integration/${randomUUID()}/`, import.meta.url));
mkdirSync(directory, { recursive: true });
const runs = join(directory, 'runs'), owned = new Set(), evidence = [], cleanup = [];
let session = await connect({ runs }), primary;
async function ok(tool, args = {}) {
  const job = await operation(session, tool, args);
  evidence.push({ tool, job_id: job.job_id, status: job.status, result: job.result, error: job.error, cleanup: job.cleanup });
  assert.equal(job.status, 'succeeded', JSON.stringify(job.error)); assert.equal(job.cleanup?.completed, true);
  return job;
}
async function create(width_mm, height_mm) {
  const result = await ok('corel_create_document', { request_id: randomUUID(), width_mm, height_mm });
  owned.add(result.result.document_id); return result;
}
const inspect = document_id => ok('corel_inspect_document', { document_id, recursive: true, include_text: true });
async function close(document_id) {
  await ok('corel_close_document', { request_id: randomUUID(), document_id, discard_changes: true });
  owned.delete(document_id); cleanup.push({ document_id, closed: true });
}
try {
  const status = await ok('corel_status'); assert.match(status.versions.coreldraw, /^27\./);
  assert.equal(status.result.executor.backend, 'csharp'); assert(!Object.hasOwn(status.result, 'vba'));
  for (const tool of ['corel_execute_vba', 'corel_build_vba'])
    await assert.rejects(session.client.callTool({ name: tool, arguments: {} }), /not found/);
  const first = await create(100, 80), doc = first.result.document_id;
  const initial = (await inspect(doc)).result, layer_id = initial.layers.find(v => !v.is_special_layer).layer_id;
  const control = (await create(40, 50)).result.document_id, controlBefore = (await inspect(control)).result;
  const draw = (tool, args) => ok(tool, { request_id: randomUUID(), document_id: doc, page_index: 1, ...args });
  const geometry = (await draw('corel_create_shapes', { layer_id, items: [{ kind: 'rectangle', x_mm: 10, y_mm: 10, width_mm: 45, height_mm: 25 }] })).result;
  const rectangle = geometry.created_static_ids[0];
  await draw('corel_style_shapes', { static_ids: [rectangle], fill: { kind: 'uniform', color: { model: 'rgb', r: 45, g: 125, b: 230 } }, outline: { kind: 'none' } });
  await draw('corel_create_text', { kind: 'artistic', layer_id, text: 'Привет CorelDRAW', x_mm: 12, y_mm: 45, font_family: 'Arial', font_size_pt: 12 });
  await draw('corel_manage_pages', { action: 'add', count: 1, width_mm: 100, height_mm: 80 });
  const page = (await ok('corel_inspect_document', { document_id: doc, limit: 1 })).result;
  assert.equal(page.total_shapes, 2); assert.equal(page.total_pages, 2); assert.equal(page.pages_truncated, true);
  assert.equal(page.shapes.length, 1); assert.equal(page.next_offset, 1);
  const next = (await ok('corel_inspect_document', { document_id: doc, limit: 1, offset: 1 })).result;
  assert.notEqual(page.shapes[0].static_id, next.shapes[0].static_id);
  const measured = (await ok('corel_measure_shapes', { document_id: doc, page_index: 1, static_ids: [rectangle] })).result;
  assert.equal(measured.original_state_preserved, true); assert(Math.abs(measured.objects[0].min_width_mm - 25) < 0.001);
  assert(Math.abs(measured.objects[0].rectangle.area_mm2 - 1125) < 0.001);
  const dirty = await operation(session, 'corel_close_document', { request_id: randomUUID(), document_id: doc });
  assert.equal(dirty.status, 'failed'); assert.equal(dirty.error.code, 'UNSAVED_CHANGES'); assert.equal(dirty.error.applied, false);
  const path = join(directory, 'Макет с пробелами.cdr');
  await ok('corel_save_document', { request_id: randomUUID(), document_id: doc, path });
  const beforeExport = (await inspect(doc)).result;
  for (const format of ['png', 'pdf', 'svg', 'jpeg', 'tiff', 'ai', 'eps', 'dxf', 'plt']) {
    const output = join(directory, `result.${format}`); writeFileSync(output, 'sentinel');
    await ok('corel_export', { request_id: randomUUID(), document_id: doc, path: output, format, page_index: 1 });
    const bytes = readFileSync(output); assert(bytes.length > 10); assert.notEqual(bytes.toString(), 'sentinel');
  }
  await ok('corel_export', { request_id: randomUUID(), document_id: doc, path: join(directory, 'all-pages.pdf'), format: 'pdf', range: 'document' });
  const preview = await ok('corel_preview', { document_id: doc, max_size: 600 });
  const png = readFileSync(preview.result.path); assert.equal(Math.max(png.readUInt32BE(16), png.readUInt32BE(20)), 600);
  const image = await session.client.callTool({ name: 'corel_get_job', arguments: { job_id: preview.job_id, include_preview: true } }); assert(image.content.some(block => block.type === 'image'));
  assert.deepEqual((await inspect(doc)).result, beforeExport); assert.deepEqual((await inspect(control)).result, controlBefore);
  assert.equal((await ok('corel_status')).result.active_document_id, control);
  await close(doc); await close(control);
  const stale = await operation(session, 'corel_inspect_document', { document_id: doc }); assert.equal(stale.error.code, 'STALE_DOCUMENT'); assert.equal(stale.error.applied, false);
  await session.close(); session = await connect({ runs });
  const persisted = await call(session, 'corel_get_job', { job_id: first.job_id }); assert.equal(persisted.status, 'succeeded');
  const opened = (await ok('corel_open_document', { request_id: randomUUID(), path })).result.document_id; owned.add(opened);
  assert.notEqual(opened, doc); assert.equal((await inspect(opened)).result.total_shapes, 2); await close(opened);
} catch (error) { primary = error; }
finally {
  for (const document_id of [...owned].reverse()) try { await close(document_id); }
  catch (error) { cleanup.push({ document_id, closed: false, error: String(error) }); primary ??= error; }
  writeFileSync(join(directory, 'verification.json'), JSON.stringify({ passed: !primary, error: primary && String(primary), cleanup, evidence }, null, 2)); await session.close();
}
if (primary) throw primary;
console.log(`Документы, метаданные, измерения и экспорт проверены: ${directory}`);
