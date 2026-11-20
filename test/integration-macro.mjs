import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { call, connect, operation } from './helpers.mjs';

if (process.env.CORELDRAW_WORKER_LIVE !== '1') throw new Error('Требуется CORELDRAW_WORKER_LIVE=1.');
if (!process.env.CORELDRAW_MCP_DUPLICATE_DEMO_GMS || !/^[a-fA-F0-9]{64}$/.test(process.env.CORELDRAW_MCP_DUPLICATE_DEMO_SHA256 ?? ''))
  throw new Error('Требуется указать подготовленный GMS и его SHA-256.');
const directory = fileURLToPath(new URL(`../artifacts/integration-macro/${randomUUID()}/`, import.meta.url));
mkdirSync(directory, { recursive: true });
const session = await connect({ runs: join(directory, 'runs') });
const owned = [];
const evidence = [];
let error;
let retain = false;
async function ok(tool, args = {}) {
  const job = await operation(session, tool, args);
  evidence.push({ tool, job_id: job.job_id, status: job.status, result: job.result, error: job.error, cleanup: job.cleanup });
  if (job.status === 'unknown' || job.status === 'timed_out' ||
      job.error?.code === 'NATIVE_OUTCOME_UNKNOWN' || job.cleanup?.completed === false) retain = true;
  assert.equal(job.status, 'succeeded', JSON.stringify(job.error));
  assert.equal(job.cleanup?.completed, true);
  return job;
}
async function create() {
  const { result } = await ok('corel_create_document', { request_id: randomUUID(), width_mm: 180, height_mm: 75 });
  owned.push(result.document_id);
  return result.document_id;
}
const inspect = async document_id => (await ok('corel_inspect_document', { document_id, page_index: 1 })).result;
try {
  const catalog = await call(session, 'corel_list_macros');
  assert.equal(catalog.macros.find(macro => macro.macro_id === 'duplicate_demo').configured, true);
  const document_id = await create();
  const page_index = 1;
  const layer_id = (await inspect(document_id)).layers.find(layer => !layer.is_special_layer).layer_id;
  const source = await ok('corel_create_shapes', { request_id: randomUUID(), document_id, page_index, layer_id,
    items: [{ kind: 'rectangle', x_mm: 13, y_mm: 24, width_mm: 26, height_mm: 18 }] });
  const static_ids = source.result.created_static_ids;
  await ok('corel_style_shapes', { request_id: randomUUID(), document_id, page_index, static_ids, outline: { kind: 'none' } });
  const before = await inspect(document_id);
  const control = await create();
  const controlBefore = await inspect(control);
  const args = { request_id: randomUUID(), document_id, page_index, macro_id: 'duplicate_demo', static_ids, parameters: { count: 3 } };
  const job = await ok('corel_run_macro', args);
  assert.equal(job.versions.execution_engine, 'vba_gms');
  assert.equal(job.result.vba_status, 'ok');
  assert.equal(job.result.created_static_ids.length, 3);
  assert.equal((await call(session, 'corel_run_macro', args)).job_id, job.job_id);
  const after = await inspect(document_id);
  assert.equal(after.total_shapes, 4);
  assert.deepEqual(after.selection_ids, before.selection_ids);
  assert.deepEqual((await inspect(control)), controlBefore);
  const x = after.shapes.map(shape => shape.bounds_mm.left).sort((a, b) => a - b);
  for (const [index, value] of x.entries()) assert(Math.abs(value - (13 + 31 * index)) < 0.001);
} catch (caught) {
  error = caught;
} finally {
  if (!retain) for (const document_id of [...owned].reverse()) {
    try { await ok('corel_close_document', { request_id: randomUUID(), document_id, discard_changes: true }); }
    catch (caught) { error ??= caught; if (retain) break; }
  }
  writeFileSync(join(directory, 'verification.json'), JSON.stringify({ passed: !error, retain, error: error && String(error), evidence }, null, 2));
  if (!retain) await session.close();
  else console.error(`Проверьте исход задания в ${directory}; рисунки оставлены открытыми.`);
}
if (error) throw error;
console.log(`Тест VBA завершён. Отчёт ${directory}`);
