import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { connect, operation, call } from './helpers.mjs';

const root = await mkdtemp(join(process.env.CORELDRAW_MCP_INTEGRATION_PARENT || process.env.LOCALAPPDATA, 'CorelDrawMcp-integration-'));
const runs = join(root, 'runs');
let session = await connect({ runs });
const drawings = new Set();
const checks = [];
async function check(label, body) {
  const value = await body(); checks.push(label); console.log(`PASS ${label}`); return value;
}
async function ok(tool, args) {
  const report = await operation(session, tool, args);
  assert.equal(report.status, 'succeeded', JSON.stringify(report, null, 2));
  assert.equal(report.cleanup.completed, true, JSON.stringify(report));
  return report;
}
const request = name => ({ request_id: `integration-${name}` });
try {
  const status = await check('CorelDRAW 27 and VBA accessible', () => ok('corel_status'));
  assert.match(status.result.version, /27\./);
  const first = (await ok('corel_create_document', { ...request('create-one'), width_mm: 100, height_mm: 80 })).result;
  drawings.add(first.document_id);
  const second = (await ok('corel_create_document', { ...request('create-two'), width_mm: 40, height_mm: 50 })).result;
  drawings.add(second.document_id);
  const doc = { document_id: first.document_id };
  await check('Two documents have separate identities', async () => assert.notEqual(first.document_id, second.document_id));
  const geometry = `Option Explicit
Public Function Main() As String
    Dim s As Shape, t As Shape, added As Page, extra As Layer
    ActiveDocument.Unit = cdrMillimeter
    Set s = ActiveLayer.CreateRectangle2(10, 10, 45, 25)
    s.Name = "MCP test rectangle"
    s.Fill.ApplyUniformFill CreateRGBColor(45, 125, 230)
    Set t = ActiveLayer.CreateArtisticText(12, 45, "Привет CorelDRAW")
    Set added = ActiveDocument.AddPages(1)
    added.ActiveLayer.CreateRectangle2 5, 5, 10, 10
    ActiveDocument.Pages(1).Activate
    Set extra = ActivePage.CreateLayer("MCP extra")
    s.CreateSelection
    t.AddToSelection
    Main = "{""created"":true,""static_id"":" & CStr(s.StaticID) & "}"
End Function`;
  const execution = await check('Compile/execute VBA and return JSON', () => ok('corel_execute_vba', { ...request('geometry'), ...doc, code: geometry }));
  assert.equal(execution.result.value.created, true);
  assert.equal(execution.cleanup.project_retained, false);
  await check('Inspect target while a different document is active', async () => {
    const report = await ok('corel_inspect_document', { ...doc, limit: 1 });
    assert.equal(report.result.total_shapes, 2);
    assert.equal(report.result.shapes.length, 1);
    assert.equal(report.result.next_offset, 1);
    for (const key of ['pages', 'layers', 'selection_ids']) assert.equal(report.result[key].length, 1);
    assert.equal(report.result.total_pages, 2); assert.equal(report.result.pages_truncated, true);
    assert.equal(report.result.total_selected, 2); assert.equal(report.result.selection_truncated, true);
    assert.equal(report.result.layers_truncated, true);
    const complete = await ok('corel_inspect_document', doc);
    assert(complete.result.selection_ids.includes(execution.result.value.static_id));
    const next = await ok('corel_inspect_document', { ...doc, limit: 1, offset: 1 });
    assert.equal(next.result.next_offset, null);
    assert.notEqual(next.result.shapes[0].static_id, report.result.shapes[0].static_id);
  });
  await check('Dirty-document close is refused', async () => {
    const report = await operation(session, 'corel_close_document', { ...request('refuse-dirty'), ...doc });
    assert.equal(report.error.code, 'UNSAVED_CHANGES');
  });
  const cdr = join(root, 'Макет с пробелами.cdr');
  await check('Save Unicode CDR path', () => ok('corel_save_document', { ...request('save'), ...doc, path: cdr }));
  await check('Open already loaded CDR returns same ID', async () => {
    const report = await ok('corel_open_document', { ...request('open-loaded'), path: cdr });
    assert.equal(report.result.document_id, first.document_id);
  });
  await check('Export PNG, SVG, PDF', async () => {
    for (const format of ['png', 'svg', 'pdf']) {
      const path = join(root, `Экспорт ${format}.${format}`);
      await ok('corel_export', { ...request(`export-${format}`), ...doc, format, path });
      const bytes = await readFile(path);
      if (format === 'png') assert.equal(bytes.subarray(1, 4).toString(), 'PNG');
      if (format === 'pdf') assert.equal(bytes.subarray(0, 4).toString(), '%PDF');
      if (format === 'svg') assert.match(bytes.toString(), /<svg\b/);
    }
  });
  await check('Overwrite protections', async () => {
    const output = join(root, 'existing.png'); await writeFile(output, 'sentinel');
    const denied = await operation(session, 'corel_export', { ...request('deny-export'), ...doc, format: 'png', path: output });
    assert.equal(denied.error.code, 'FILE_EXISTS'); assert.equal(await readFile(output, 'utf8'), 'sentinel');
    await ok('corel_export', { ...request('replace-export'), ...doc, format: 'png', path: output, overwrite: true });
    assert.equal((await readFile(output)).subarray(1, 4).toString(), 'PNG');
    const otherCdr = join(root, 'existing.cdr'); await writeFile(otherCdr, 'sentinel');
    const saveDenied = await operation(session, 'corel_save_document', { ...request('deny-save'), ...doc, path: otherCdr });
    assert.equal(saveDenied.error.code, 'FILE_EXISTS'); assert.equal(await readFile(otherCdr, 'utf8'), 'sentinel');
  });
  await check('PNG/SVG selection and whole-document PDF', async () => {
    for (const format of ['png', 'svg']) await ok('corel_export', { ...request(`selection-${format}`), ...doc, range: 'selection', format, path: join(root, `selection.${format}`) });
    await ok('corel_export', { ...request('pdf-document'), ...doc, format: 'pdf', range: 'document', path: join(root, 'document.pdf') });
  });
  await check('Preview returns a bounded MCP image', async () => {
    const preview = await ok('corel_preview', { ...doc, max_size: 600 });
    const image = await session.client.callTool({ name: 'corel_get_job', arguments: { job_id: preview.job_id, include_preview: true } });
    assert(image.content.some(block => block.type === 'image'));
    const png = await readFile(preview.result.path);
    assert.equal(Math.max(png.readUInt32BE(16), png.readUInt32BE(20)), 600);
    execFileSync('powershell.exe', ['-NoLogo', '-NoProfile', '-File', fileURLToPath(new URL('./check-preview.ps1', import.meta.url)), '-Path', preview.result.path], { windowsHide: true });
  });
  const sources = join(root, 'Исходники VBA'); await mkdir(sources);
  await writeFile(join(sources, 'M_Valid.bas'), 'Attribute VB_Name = "M_Valid"\nOption Explicit\nPublic Function Answer() As Long\nAnswer = 42\nEnd Function\n', 'utf8');
  await check('Build standard UTF-8 modules without executing them', () => ok('corel_build_vba', { ...request('build'), source_root: sources }));
  await check('Runtime errors retain GMS and restore application settings', async () => {
    const before = (await ok('corel_status')).result;
    const beforeDoc = (await ok('corel_inspect_document', doc)).result.document;
    const code = `Option Explicit
Public Function Main() As String
    Application.Optimization = False
    Application.EventsEnabled = True
    ActiveDocument.Unit = cdrInch
    ActiveDocument.PreserveSelection = ${beforeDoc.preserve_selection ? 'False' : 'True'}
    Err.Raise 555, "Integration", "Expected error"
End Function`;
    const report = await operation(session, 'corel_execute_vba', { ...request('runtime-fail'), ...doc, code });
    assert.equal(report.status, 'failed'); assert.equal(report.error.code, 'VBA_RUNTIME_ERROR');
    assert.equal(report.error.number, 555); assert.equal(report.cleanup.completed, true); assert.equal(report.cleanup.project_retained, true);
    const after = (await ok('corel_status')).result;
    assert.deepEqual(after.application_state, before.application_state);
    assert.equal(after.active_document_id, before.active_document_id);
    const afterDoc = (await ok('corel_inspect_document', doc)).result.document;
    assert.equal(afterDoc.unit, beforeDoc.unit);
    assert.equal(afterDoc.preserve_selection, beforeDoc.preserve_selection);
  });
  await check('JSON arrays, scalars and null retain their exact types', async () => {
    for (const [index, json] of ['[]', '[1]', '[null]', 'null', 'false', '{"Ёж":"Привет"}'].entries()) {
      const code = `Option Explicit\nPublic Function Main() As String\nMain = "${json.replaceAll('"', '""')}"\nEnd Function`;
      const report = await ok('corel_execute_vba', { ...request(`json-${index}`), ...doc, code });
      assert.deepEqual(report.result.value, JSON.parse(json));
    }
  });
  await check('Empty and non-standard JSON retain diagnostic GMS', async () => {
    for (const [index, json] of [' ', '{"trailing":true,}'].entries()) {
      const code = `Option Explicit\nPublic Function Main() As String\nMain = "${json.replaceAll('"', '""')}"\nEnd Function`;
      const report = await operation(session, 'corel_execute_vba', { ...request(`bad-json-${index}`), ...doc, code });
      assert.equal(report.status, 'failed'); assert.equal(report.error.code, 'INVALID_VBA_JSON');
      assert.equal(report.cleanup.project_retained, true); assert.equal(report.cleanup.completed, true);
    }
  });
  await check('Packaged geometry examples create and move selected shapes', async () => {
    const createCode = await readFile(new URL('../skills/coreldraw-automation/examples/M_CreateGeometry.bas', import.meta.url), 'utf8');
    const moveCode = await readFile(new URL('../skills/coreldraw-automation/examples/M_MoveSelection.bas', import.meta.url), 'utf8');
    const created = await ok('corel_execute_vba', { ...request('example-create'), ...doc, code: createCode });
    const id = created.result.value.static_id;
    const before = (await ok('corel_inspect_document', doc)).result.shapes.find(shape => shape.static_id === id);
    const moved = await ok('corel_execute_vba', { ...request('example-move'), ...doc, code: moveCode });
    assert.equal(moved.result.value.moved_count, 1);
    const after = (await ok('corel_inspect_document', doc)).result.shapes.find(shape => shape.static_id === id);
    assert.equal(after.bounds.left - before.bounds.left, 5);
  });
  await writeFile(join(sources, 'M_Invalid.bas'), 'Option Explicit\nPublic Sub NeverCalled()\nDim value As DoesNotExist\nEnd Sub\n', 'utf8');
  await check('Full compilation catches an uncalled procedure', async () => {
    for (let index = 0; index < 3; index++) {
      const report = await operation(session, 'corel_build_vba', { ...request(`compile-fail-${index}`), source_root: sources });
      assert.equal(report.status, 'failed'); assert.equal(report.error.code, 'COMPILATION_FAILED');
      assert.equal(report.cleanup.project_retained, true);
      assert.equal(report.compiler_location?.module, 'M_Invalid');
      assert.equal(report.compiler_location.line, 3);
      assert.match(report.compiler_location.text, /DoesNotExist/);
    }
  });
  await check('Closed document IDs become stale and reopened files get new IDs', async () => {
    await ok('corel_close_document', { ...request('close-first'), ...doc, discard_changes: true }); drawings.delete(first.document_id);
    const stale = await operation(session, 'corel_inspect_document', doc); assert.equal(stale.error.code, 'STALE_DOCUMENT');
    const reopened = (await ok('corel_open_document', { ...request('reopen'), path: cdr })).result; drawings.add(reopened.document_id);
    assert.notEqual(reopened.document_id, first.document_id);
  });
  await check('Executor reconnection invalidates earlier document IDs', async () => {
    await ok('corel_close_document', { ...request('close-second'), document_id: second.document_id, discard_changes: true }); drawings.delete(second.document_id);
    const [oldId] = drawings;
    await session.close();
    session = await connect({ runs });
    const reopened = (await ok('corel_open_document', { ...request('after-reconnect'), path: cdr })).result;
    drawings.delete(oldId); drawings.add(reopened.document_id);
    assert.notEqual(reopened.document_id, oldId);
    const stale = await operation(session, 'corel_inspect_document', { document_id: oldId });
    assert.equal(stale.error.code, 'STALE_DOCUMENT');
  });
  await writeFile(join(root, 'verification.json'), JSON.stringify({ ok: true, checks, root, date: new Date().toISOString() }, null, 2));
  console.log(`Integration report: ${root}`);
} finally {
  for (const document_id of drawings) {
    try { await operation(session, 'corel_close_document', { request_id: `cleanup-${document_id}`, document_id, discard_changes: true }); }
    catch (error) { console.error(`Cleanup required for ${document_id}: ${error.message}`); }
  }
  console.error(session.stderr());
  await session.close();
}
