import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { connect, operation } from './helpers.mjs';

if (process.env.CORELDRAW_WORKER_LIVE !== '1' || process.env.CORELDRAW_MCP_BACKEND !== 'csharp') throw new Error('Нужны CORELDRAW_WORKER_LIVE=1 и CORELDRAW_MCP_BACKEND=csharp.');
const directory = fileURLToPath(new URL(`../artifacts/integration-production/${randomUUID()}/`, import.meta.url));
mkdirSync(directory, { recursive: true });
const session = await connect({ runs: join(directory, 'runs') });
const owned = [], evidence = [], cleanup = [];
let primary;
async function ok(tool, args = {}) {
  const job = await operation(session, tool, args); evidence.push({ tool, job_id: job.job_id, status: job.status, result: job.result, error: job.error, cleanup: job.cleanup });
  assert.equal(job.status, 'succeeded', JSON.stringify(job.error)); assert.equal(job.cleanup?.completed, true); return job.result;
}
try {
  const doc = await ok('corel_create_document', { request_id: randomUUID() }); owned.push(doc.document_id);
  const binding = { document_id: doc.document_id, page_index: 1 };
  const inspection = await ok('corel_inspect_document', { document_id: doc.document_id });
  const layer_id = inspection.layers.find(layer => !layer.is_special_layer).layer_id;
  const draw = (tool, args) => ok(tool, { request_id: randomUUID(), ...binding, ...args });
  const created = await draw('corel_create_curve', { layer_id, subpaths: [{ start: { x_mm: 10, y_mm: 10 }, closed: false, segments: [{ kind: 'line', to: { x_mm: 40, y_mm: 10 } }, { kind: 'line', to: { x_mm: 40, y_mm: 30 } }] }] });
  const static_id = created.created_static_ids[0];
  const before = await ok('corel_inspect_curve', { ...binding, static_id }); assert.equal(before.total_nodes, 3);
  const preflight = await ok('corel_preflight', { ...binding, static_ids: [static_id], checks: [{ kind: 'open_contours' }] });
  assert.equal(preflight.objects[0].checks[0].status, 'failed');
  await draw('corel_edit_curve', { static_id, expected_revision: before.geometry_revision, action: 'close_subpath', subpath_index: 1 });
  const stale = await operation(session, 'corel_edit_curve', { request_id: randomUUID(), ...binding, static_id, expected_revision: before.geometry_revision, action: 'move_node', subpath_index: 1, node_index: 1, x_mm: 0, y_mm: 0 });
  assert.equal(stale.status, 'failed'); assert.equal(stale.error.code, 'STALE_GEOMETRY'); assert.equal(stale.error.applied, false);
  await draw('corel_style_shapes', { static_ids: [static_id], fill: { kind: 'none' }, outline: { kind: 'uniform', width_mm: 0.2, color: { model: 'rgb', r: 255, g: 0, b: 0 } } });
  const final = await ok('corel_preflight', { ...binding, static_ids: [static_id], checks: [{ kind: 'open_contours' }, { kind: 'outline_width', expected_mm: 0.2, tolerance_mm: 0.001 }, { kind: 'outline_color', color: { model: 'rgb', r: 255, g: 0, b: 0 }, tolerance: 0 }] });
  assert(final.objects[0].checks.every(check => check.status === 'passed'));
  const text = await draw('corel_create_text', { layer_id, kind: 'artistic', text: 'Ёж ёж', x_mm: 10, y_mm: 50, font_family: 'Arial', font_size_pt: 12 });
  await draw('corel_edit_text', { static_ids: text.created_static_ids, action: 'replace_literal', old: 'ёж', new: 'Лис', case_sensitive: false });
  const found = await ok('corel_find_text', { ...binding, query: 'лис', case_sensitive: false, include_text: true });
  assert.equal(found.matches[0].count, 2); assert.equal(found.matches[0].snippet, 'Лис Лис');
  await ok('corel_preview', { document_id: doc.document_id, max_size: 600 });
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
console.log(`Производственный сценарий прошёл: ${directory}`);
