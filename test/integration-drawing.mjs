import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { connect, operation } from './helpers.mjs';

if (process.env.CORELDRAW_WORKER_LIVE !== '1' || process.env.CORELDRAW_MCP_BACKEND !== 'csharp')
  throw new Error('Нужны явные CORELDRAW_WORKER_LIVE=1 и CORELDRAW_MCP_BACKEND=csharp.');
const directory = fileURLToPath(new URL(`../artifacts/integration-drawing/${randomUUID()}/`, import.meta.url));
mkdirSync(directory, { recursive: true });
const session = await connect({ runs: join(directory, 'runs') });
const owned = [], evidence = [], cleanup = [];
let primary;
async function ok(tool, args = {}) {
  const job = await operation(session, tool, args);
  evidence.push({ tool, args, job_id: job.job_id, status: job.status, result: job.result, error: job.error, cleanup: job.cleanup });
  assert.equal(job.status, 'succeeded', JSON.stringify(job.error)); assert.equal(job.cleanup?.completed, true);
  return job.result;
}
async function create() { const result = await ok('corel_create_document', { request_id: randomUUID() }); owned.push(result.document_id); return result.document_id; }
try {
  const tools = (await session.client.listTools()).tools;
  assert(!tools.some(tool => /corel_(execute|build)_vba/.test(tool.name)));
  const doc = await create();
  const before = await ok('corel_inspect_document', { document_id: doc });
  const layer = before.layers.find(layer => !layer.is_special_layer).layer_id;
  const control = await create();
  const controlBefore = await ok('corel_inspect_document', { document_id: control, include_text: true, recursive: true });
  const draw = (tool, args) => ok(tool, { request_id: randomUUID(), document_id: doc, page_index: 1, ...args });
  const created = await draw('corel_create_shapes', { layer_id: layer, items: [
    { kind: 'rectangle', x_mm: 10, y_mm: 10, width_mm: 30, height_mm: 20 },
    { kind: 'ellipse', x_mm: 30, y_mm: 10, width_mm: 20, height_mm: 20 },
    { kind: 'line', x1_mm: 10, y1_mm: 60, x2_mm: 40, y2_mm: 70 },
  ] });
  const [rect, ellipse, line] = created.created_static_ids;
  const style = await draw('corel_style_shapes', { static_ids: [rect, ellipse], fill: { kind: 'uniform', color: { model: 'rgb', r: 40, g: 120, b: 210 } }, outline: { kind: 'uniform', width_mm: 0.2, color: { model: 'cmyk', c: 0, m: 0, y: 0, k: 100 } } });
  assert.equal(style.items[0].fill_color.b, 210);
  const copies = await draw('corel_duplicate_shapes', { static_ids: [rect], copies: 2, step_x_mm: 40, step_y_mm: 0 });
  assert.equal(copies.created_static_ids.length, 2);
  await draw('corel_transform_shapes', { static_ids: [line], action: 'rotate', angle_deg: 30, anchor_x_mm: 10, anchor_y_mm: 60 });
  const newLayer = await draw('corel_manage_layers', { action: 'create', page_id: before.page_id, name: 'Типизированный рецепт' });
  await draw('corel_manage_layers', { action: 'move_shapes', layer_id: newLayer.layer.layer_id, static_ids: copies.created_static_ids });
  const group = await draw('corel_group_shapes', { action: 'group', static_ids: copies.created_static_ids });
  await draw('corel_group_shapes', { action: 'ungroup', static_ids: group.created_static_ids });
  for (const action of ['weld', 'trim', 'intersect']) {
    const boolean = await draw('corel_boolean_shapes', { action, left_id: rect, right_id: ellipse, keep_left: true, keep_right: true });
    assert.equal(boolean.items.length, 1);
    assert.equal(boolean.deleted_static_ids.length, 0);
  }
  const text = await draw('corel_create_text', { kind: 'artistic', layer_id: layer, text: 'Ёж — кириллица', x_mm: 10, y_mm: 100, font_family: 'Arial', font_size_pt: 14 });
  assert.equal(text.items[0].text, 'Ёж — кириллица');
  const curves = await draw('corel_convert_to_curves', { static_ids: text.created_static_ids });
  assert(curves.items[0].type === 3 || curves.items[0].type === 7);
  assert.ok(Array.isArray(curves.id_mappings), 'Conversion must map each source ID to observed result IDs.');
  assert.equal(curves.id_mappings[0].source_static_id, text.created_static_ids[0]);
  assert(curves.id_mappings[0].result_static_ids.includes(curves.items[0].static_id));
  await ok('corel_preview', { document_id: doc, max_size: 600 });
  assert.deepEqual(await ok('corel_inspect_document', { document_id: control, include_text: true, recursive: true }), controlBefore);
  assert.equal((await ok('corel_status')).active_document_id, control);
} catch (error) { primary = error; }
finally {
  for (const document_id of owned.reverse()) {
    try { await ok('corel_close_document', { request_id: randomUUID(), document_id, discard_changes: true }); cleanup.push({ document_id, closed: true }); }
    catch (error) { cleanup.push({ document_id, closed: false, error: String(error) }); primary ??= error; }
  }
  writeFileSync(join(directory, 'verification.json'), JSON.stringify({ passed: !primary, error: primary && String(primary), cleanup, evidence }, null, 2));
  await session.close();
}
if (primary) throw primary;
console.log(`Типизированные рецепты прошли: ${directory}`);
