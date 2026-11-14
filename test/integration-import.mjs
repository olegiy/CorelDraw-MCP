import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { connect, operation } from './helpers.mjs';

if (process.env.CORELDRAW_WORKER_LIVE !== '1' || process.env.CORELDRAW_MCP_BACKEND !== 'csharp')
  throw new Error('Нужны CORELDRAW_WORKER_LIVE=1 и CORELDRAW_MCP_BACKEND=csharp.');
const directory = fileURLToPath(new URL(`../artifacts/import/${randomUUID()}/`, import.meta.url));
mkdirSync(directory, { recursive: true });
const session = await connect({ runs: join(directory, 'runs') });
const owned = [], evidence = [], cleanup = []; let primary;
async function ok(tool, args = {}) {
  const job = await operation(session, tool, args);
  evidence.push({ tool, job_id: job.job_id, status: job.status, result: job.result, error: job.error, cleanup: job.cleanup });
  assert.equal(job.status, 'succeeded', JSON.stringify(job.error)); assert.equal(job.cleanup?.completed, true);
  return job.result;
}
async function create() {
  const result = await ok('corel_create_document', { request_id: randomUUID(), width_mm: 200, height_mm: 150 });
  owned.push(result.document_id); return result.document_id;
}
const inspect = document_id => ok('corel_inspect_document', { document_id, recursive: true, include_text: true });
function fingerprint(path) {
  const bytes = readFileSync(path); return { path, size_bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') };
}
try {
  const fixture = await create(), fixtureLayer = (await inspect(fixture)).layers.find(v => !v.is_special_layer).layer_id;
  await ok('corel_create_shapes', { request_id: randomUUID(), document_id: fixture, page_index: 1, layer_id: fixtureLayer,
    items: [{ kind: 'rectangle', x_mm: 10, y_mm: 10, width_mm: 40, height_mm: 20 }] });
  const inputs = [];
  for (const format of ['png', 'jpeg', 'pdf']) {
    const path = join(directory, `fixture.${format}`);
    await ok('corel_export', { request_id: randomUUID(), document_id: fixture, path, format, page_index: 1, dpi: 50 });
    inputs.push({ format, ...fingerprint(path), ...(format === 'pdf' ? { pdf_page: 1 } : {}) });
  }
  const svg = join(directory, 'Кириллица с пробелами.svg');
  writeFileSync(svg, '<svg xmlns="http://www.w3.org/2000/svg" width="40mm" height="20mm" viewBox="0 0 40 20"><rect width="40" height="20" fill="#ff0000"/></svg>');
  inputs.push({ format: 'svg', ...fingerprint(svg) });
  const fixtureBefore = await inspect(fixture);
  const doc = await create(), initial = await inspect(doc), layer_id = initial.layers.find(v => !v.is_special_layer).layer_id;
  const control = await create(), controlBefore = await inspect(control);
  const draw = (tool, args) => ok(tool, { request_id: randomUUID(), document_id: doc, page_index: 1, ...args });
  let root;
  for (const [index, input] of inputs.entries()) {
    const result = await draw('corel_import', { ...input, layer_id, x_mm: 5 + index * 35, y_mm: 20, fit: { width_mm: 25, height_mm: 25 } });
    assert(result.created_static_ids.length > 0); assert.equal(result.source_sha256, input.sha256);
    assert(Math.abs(result.import_bounds_mm.left - (5 + index * 35)) < 0.001);
    assert(result.import_bounds_mm.width <= 25.001 && result.import_bounds_mm.height <= 25.001);
    assert.equal(fingerprint(input.path).sha256, input.sha256); root ??= result.imported_root_static_ids[0];
  }
  const made = await draw('corel_create_shapes', { layer_id, items: [{ kind: 'rectangle', x_mm: 0, y_mm: 0, width_mm: 180, height_mm: 100 }] });
  const container_id = made.created_static_ids[0];
  const placed = await draw('corel_powerclip', { action: 'place', container_id, static_ids: [root], center: false });
  const extracted = await draw('corel_powerclip', { action: 'extract', container_id });
  assert.equal(extracted.container.static_id, container_id);
  assert.deepEqual(extracted.contents.map(v => v.bounds_mm), placed.contents.map(v => v.bounds_mm));
  const added = await draw('corel_manage_pages', { action: 'add', count: 1, width_mm: 100, height_mm: 80 });
  assert.equal(added.pages.length, 1);
  await draw('corel_manage_pages', { action: 'resize', page_id: initial.page_id, width_mm: 190, height_mm: 140 });
  const pages = (await inspect(doc)).pages; assert.equal(pages[1].width, 100); assert.equal(pages[1].height, 80);
  const beforeFailure = await inspect(doc);
  writeFileSync(svg, '<svg xmlns="http://www.w3.org/2000/svg"><text>Ёж 🦔</text></svg>');
  const rejected = await operation(session, 'corel_import', { request_id: randomUUID(), document_id: doc, page_index: 1,
    format: 'svg', ...fingerprint(svg), layer_id, x_mm: 0, y_mm: 0 });
  evidence.push({ tool: 'corel_import', status: rejected.status, error: rejected.error, cleanup: rejected.cleanup });
  assert.equal(rejected.status, 'failed'); assert.equal(rejected.error.code, 'TEXT_UNSUPPORTED_CHARACTER'); assert.equal(rejected.error.applied, false);
  assert.deepEqual(await inspect(doc), beforeFailure);
  await ok('corel_preview', { document_id: doc, max_size: 600 });
  assert.deepEqual(await inspect(fixture), fixtureBefore); assert.deepEqual(await inspect(control), controlBefore);
  assert.equal((await ok('corel_status')).active_document_id, control);
} catch (error) { primary = error; }
finally {
  for (const document_id of owned.reverse()) try {
    await ok('corel_close_document', { request_id: randomUUID(), document_id, discard_changes: true }); cleanup.push({ document_id, closed: true });
  } catch (error) { cleanup.push({ document_id, closed: false, error: String(error) }); primary ??= error; }
  writeFileSync(join(directory, 'verification.json'), JSON.stringify({ passed: !primary, error: primary && String(primary), cleanup, evidence }, null, 2));
  await session.close();
}
if (primary) throw primary;
console.log(`Импорт, страницы и PowerClip проверены: ${directory}`);
