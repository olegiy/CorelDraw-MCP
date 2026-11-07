import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { connect, operation } from './helpers.mjs';

const root = await mkdtemp(join(process.env.CORELDRAW_MCP_INTEGRATION_PARENT || process.env.LOCALAPPDATA, 'CorelDrawMcp-recipes-'));
const session = await connect({ runs: join(root, 'runs'), configRoot: root });
const owned = new Set(), checks = [];
let serial = 0;
const request = () => ({ request_id: `recipes-${++serial}` });
async function ok(tool, args = {}) {
  const job = await operation(session, tool, args, 180000);
  assert.equal(job.status, 'succeeded', JSON.stringify(job));
  assert.equal(job.cleanup?.completed, true, JSON.stringify(job));
  return job;
}
async function run(id, code, success = true) {
  const job = await operation(session, 'corel_execute_vba', { ...request(), document_id: id, code }, 180000);
  assert.equal(job.cleanup?.completed, true, JSON.stringify(job));
  if (success) assert.equal(job.status, 'succeeded', JSON.stringify(job));
  return job;
}
const main = body => `Option Explicit\nPublic Function Main() As String\n${body.replace(/(?<![.\w])ActiveLayer/g, 'ActiveDocument.ActiveLayer')}\nEnd Function`;
async function recipe(name, constants = {}) {
  let source = await readFile(new URL(`../skills/coreldraw-automation/examples/${name}.bas`, import.meta.url), 'utf8');
  for (const [key, value] of Object.entries(constants)) {
    const pattern = new RegExp(`(Const ${key} As (?:Long|String) = )[^\\r\\n]+`);
    assert(pattern.test(source), `Missing bounded input ${name}.${key}`);
    source = source.replace(pattern, `$1${typeof value === 'string' ? `"${value}"` : value}`);
  }
  const helpers = await readFile(new URL('../skills/coreldraw-automation/examples/M_CommonHelpers.bas', import.meta.url), 'utf8');
  return `${source}\n${helpers.replace(/^Option Explicit\s*/m, '')}`;
}
async function snapshot(id) {
  return (await ok('corel_inspect_document', { document_id: id, recursive: true, include_text: true })).result;
}
async function check(name, body) {
  await body(); checks.push(name); console.log(`PASS ${name}`);
}
let failure;
try {
  const initial = (await ok('corel_status')).result;
  const userIds = initial.documents.map(d => d.document_id);
  const doc = (await ok('corel_create_document', { ...request(), width_mm: 100, height_mm: 80 })).result.document_id;
  owned.add(doc);
  const other = (await ok('corel_create_document', { ...request(), width_mm: 50, height_mm: 50 })).result.document_id;
  owned.add(other);
  await run(other, main('ActiveLayer.CreateRectangle2 1,1,3,4\nMain="{}"'));
  const otherPath = join(root, 'owned-other.cdr');
  await ok('corel_save_document', { ...request(), document_id: other, path: otherPath });
  const unrelated = await snapshot(other);
  const setup = await run(doc, main('Dim a As Shape,b As Shape,t As Shape,c As Shape\nSet a=ActiveLayer.CreateRectangle2(10,10,20,20)\nSet b=ActiveLayer.CreateRectangle2(20,10,20,20)\nSet t=ActiveLayer.CreateArtisticText(2,50,"untouched")\nSet c=ActiveLayer.CreateRectangle2(1,1,5,5)\nc.ConvertToCurves\na.CreateSelection\nMain="{""a"":" & CStr(a.StaticID) & ",""b"":" & CStr(b.StaticID) & ",""text"":" & CStr(t.StaticID) & ",""curve"":" & CStr(c.StaticID) & "}"'));
  const ids = setup.result.value;
  assert.equal((await snapshot(doc)).total_shapes, 4);
  assert.deepEqual((await snapshot(other)).shapes, unrelated.shapes);
  await check('style and duplicate operate on exact requested StaticID with mm/inch conversion', async () => {
    for (const unit of ['cdrMillimeter', 'cdrInch']) {
      await run(doc, main(`ActiveDocument.Unit=${unit}\nMain="{}"`));
      for (const color of ['rgb', 'cmyk']) {
        const before = await snapshot(doc);
        const style = await run(doc, await recipe('M_FillOutline', { TARGET_STATIC_ID: ids.a, COLOR_MODE: color }));
        assert.equal(style.result.value.static_id, ids.a);
        assert(Math.abs(style.result.value.outline_width_mm - 0.5) < 0.001);
        assert.deepEqual((await snapshot(doc)).selection_ids, before.selection_ids);
      }
      const before = await snapshot(doc);
      const copy = await run(doc, await recipe('M_DuplicateOffset', { TARGET_STATIC_ID: ids.a }));
      assert.equal(copy.result.value.source_static_id, ids.a);
      assert(Math.abs(copy.result.value.offset_x_mm - 20) < 0.001);
      assert.deepEqual((await snapshot(doc)).selection_ids, before.selection_ids);
    }
  });
  await check('Unicode artistic and paragraph variants return actual text', async () => {
    for (const kind of ['artistic', 'paragraph']) {
      const before = await snapshot(doc);
      const job = await run(doc, await recipe('M_TextUnicode', { TEXT_KIND: kind }));
      assert.match(job.result.value.text, /Привет/);
      const state = await snapshot(doc);
      assert(state.shapes.some(s => s.static_id === job.result.value.static_id && s.text.includes('Привет')));
      assert.deepEqual(state.selection_ids, before.selection_ids);
    }
  });
  await check('Boolean weld trim intersect preserve requested originals and report geometry', async () => {
    for (const action of ['weld', 'trim', 'intersect']) {
      const before = await snapshot(doc);
      const job = await run(doc, await recipe('M_BooleanShapes', { LEFT_STATIC_ID: ids.a, RIGHT_STATIC_ID: ids.b, BOOLEAN_OPERATION: action }));
      assert.equal(job.result.value.left_static_id, ids.a);
      assert.equal(job.result.value.right_static_id, ids.b);
      assert(job.result.value.result_static_id > 0);
      assert(Math.abs(job.result.value.width_mm - (action === 'weld' ? 30 : 10)) < 0.01);
      assert.deepEqual((await snapshot(doc)).selection_ids, before.selection_ids);
    }
  });
  await check('curve inspection does not create, convert or modify source shapes', async () => {
    const before = await snapshot(doc);
    const job = await run(doc, await recipe('M_CurveNodes', { TARGET_STATIC_ID: ids.curve }));
    assert.equal(job.result.value.static_id, ids.curve);
    assert.equal(job.result.value.nodes, 4);
    assert.deepEqual((await snapshot(doc)).shapes, before.shapes);
  });
  await check('negative recipe inputs never mutate an unrelated or fallback object', async () => {
    const before = await snapshot(doc);
    for (const [name, constants, message] of [
      ['M_FillOutline', { TARGET_STATIC_ID: 2147483647 }, /STATIC_ID_NOT_FOUND/],
      ['M_CurveNodes', { TARGET_STATIC_ID: ids.text }, /UNSUPPORTED_SHAPE/],
      ['M_BooleanShapes', { LEFT_STATIC_ID: ids.text, RIGHT_STATIC_ID: ids.b }, /UNSUPPORTED_SHAPE/],
    ]) {
      const job = await run(doc, await recipe(name, constants), false);
      assert.equal(job.status, 'failed'); assert.match(job.error.message, message);
      assert.equal(job.error.applied, false);
      assert.deepEqual((await snapshot(doc)).shapes, before.shapes);
    }
    await run(doc, main('ActiveDocument.ClearSelection\nMain="{}"'));
    const cleared = await snapshot(doc);
    const empty = await operation(session, 'corel_preview', { document_id: doc, target: 'selection' });
    assert.equal(empty.status, 'failed'); assert.equal(empty.error.code, 'EMPTY_SELECTION');
    const malformed = await run(doc, main('Main="{broken"'), false);
    assert.equal(malformed.error.code, 'INVALID_VBA_JSON');
    const forced = await run(doc, main('ActiveDocument.Unit=cdrInch\nApplication.Optimization=True\nErr.Raise 555,"forced","FORCED_SETUP_FAILURE"'), false);
    assert.equal(forced.error.code, 'VBA_RUNTIME_ERROR');
    const after = await snapshot(doc);
    assert.deepEqual(after.shapes, cleared.shapes);
    assert.equal(after.document.unit, before.document.unit);
    assert.deepEqual((await snapshot(other)).shapes, unrelated.shapes);
  });
  await check('partial mutation and malformed JSON do not claim rollback', async () => {
    const before = await snapshot(doc);
    for (const ending of ['Err.Raise 556,"partial","PARTIAL_ACTION"', 'Main="{broken"']) {
      const job = await run(doc, main(`Dim s As Shape\nSet s=ActiveLayer.CreateRectangle2(70,5,2,3)\ns.Name="partial-marker"\n${ending}`), false);
      assert.equal(job.status, 'failed');
      assert.equal(job.cleanup.project_retained, true);
    }
    const after = await snapshot(doc);
    assert.equal(after.total_shapes, before.total_shapes + 2);
    for (const source of before.shapes) assert(after.shapes.some(s => s.static_id === source.static_id));
    assert.deepEqual((await snapshot(other)).shapes, unrelated.shapes);
  });
  await check('recipe partial action and restoration failure have structured diagnostics', async () => {
    let code = await recipe('M_FillOutline', { TARGET_STATIC_ID: ids.a });
    code = code.replace('s.Fill.ApplyUniformFill CreateRGBColor(30, 130, 90)', 's.Fill.ApplyUniformFill CreateRGBColor(30, 130, 90)\nErr.Raise 558,"test","AFTER_FILL_FAILURE"');
    const partial = await run(doc, code, false);
    assert.equal(partial.status, 'failed');
    assert.equal(partial.error.applied, true);
    assert.equal(partial.error.code, 'RECIPE_ACTION_FAILED');
    code = await recipe('M_DuplicateOffset', { TARGET_STATIC_ID: ids.a });
    code = code.replace('document.ClearSelection', 'Err.Raise 559,"test","RESTORE_SELECTION_FAILURE"');
    const cleanup = await operation(session, 'corel_execute_vba', { ...request(), document_id: doc, code });
    assert.equal(cleanup.status, 'failed');
    assert.equal(cleanup.error.applied, true);
    assert.equal(cleanup.cleanup.completed, false);
    assert(cleanup.cleanup.errors.some(e => e.step === 'recipe.selection'));
  });
  await check('preview restores page and active document and rejects empty selection on requested page', async () => {
    await run(doc, main('Dim added As Page\nSet added=ActiveDocument.AddPages(1)\nadded.ActiveLayer.CreateRectangle2 5,5,10,10\nActiveDocument.Pages(1).Activate\nActiveDocument.ClearSelection\nMain="{}"'));
    const before = await snapshot(doc), status = (await ok('corel_status')).result;
    await ok('corel_preview', { document_id: doc, target: 'page', page_index: 2, max_size: 100 });
    const after = await snapshot(doc);
    assert.equal(after.document.active_page, before.document.active_page);
    assert.deepEqual(after.selection_ids, before.selection_ids);
    const rejected = await operation(session, 'corel_preview', { document_id: doc, target: 'selection', page_index: 2 });
    assert.equal(rejected.status, 'failed');
    assert.equal(rejected.error.code, 'SELECTION_PAGE_MISMATCH');
    assert.equal(rejected.cleanup.completed, true);
    assert.equal((await snapshot(doc)).document.active_page, before.document.active_page);
    assert.equal((await ok('corel_status')).result.active_document_id, status.active_document_id);
  });
  await check('foreign-session and closed document IDs reject before VBA dispatch', async () => {
    const stale = (await ok('corel_create_document', { ...request() })).result.document_id;
    owned.add(stale);
    await ok('corel_close_document', { ...request(), document_id: stale, discard_changes: true });
    owned.delete(stale);
    for (const document_id of [stale, 'foreign-session:foreign-document']) {
      const rejected = await run(document_id, main('Err.Raise 557,"test","MUST_NOT_RUN"'), false);
      assert.equal(rejected.status, 'failed');
      assert.equal(rejected.error.code, 'STALE_DOCUMENT');
      assert.equal(rejected.artifacts.length, 0);
    }
    assert.deepEqual((await snapshot(other)).shapes, unrelated.shapes);
  });
  await check('VBA document switching is detected after Main returns', async () => {
    const before = await snapshot(other);
    const switched = await run(doc, main(`Dim candidate As Document, target As Document\nFor Each candidate In Application.Documents\nIf candidate.FullFileName="${otherPath.replaceAll('"', '""')}" Then Set target=candidate\nNext candidate\nIf target Is Nothing Then Err.Raise 5,"test","OWNED_DOCUMENT_NOT_FOUND"\ntarget.Activate\nActiveDocument.ActiveLayer.CreateRectangle2 70,70,2,2\nMain="{}"`), false);
    assert.equal(switched.status, 'failed');
    assert.equal(switched.error.code, 'TARGET_DOCUMENT_CHANGED');
    assert.equal((await snapshot(other)).total_shapes, before.total_shapes + 1);
  });
  await check('locked target layer is rejected before recipe mutation', async () => {
    await run(doc, main('ActiveLayer.Editable=False\nMain="{}"'));
    try {
      const before = await snapshot(doc);
      const job = await run(doc, await recipe('M_FillOutline', { TARGET_STATIC_ID: ids.a }), false);
      assert.equal(job.status, 'failed'); assert.match(job.error.message, /LOCKED_LAYER/);
      assert.deepEqual((await snapshot(doc)).shapes, before.shapes);
    } finally { await run(doc, main('ActiveLayer.Editable=True\nMain="{}"')); }
  });
  await check('layer grouping moves only requested StaticIDs to named layer', async () => {
    const before = await snapshot(doc);
    const job = await run(doc, await recipe('M_LayerGroup', { FIRST_STATIC_ID: ids.a, SECOND_STATIC_ID: ids.b }));
    const after = await snapshot(doc);
    const members = after.shapes.filter(s => s.parent_static_id === job.result.value.group_static_id).map(s => s.static_id).sort((a,b) => a-b);
    assert.deepEqual(members, [ids.a, ids.b].sort((a,b) => a-b));
    assert(after.shapes.some(s => s.static_id === ids.text));
    assert.equal(after.total_shapes, before.total_shapes - 1);
    assert.deepEqual(after.selection_ids, before.selection_ids);
  });
  await check('bounded lookup continues after a depth-limited group', async () => {
    const helpers = await readFile(new URL('../skills/coreldraw-automation/examples/M_CommonHelpers.bas', import.meta.url), 'utf8');
    const code = main('Dim a As Shape,b As Shape,g As Shape,r As ShapeRange,found As Shape\nSet a=ActiveLayer.CreateRectangle2(1,1,1,1)\nSet b=ActiveLayer.CreateRectangle2(3,1,1,1)\nSet r=CreateShapeRange\nr.Add a\nr.Add b\nSet g=r.Group\ng.OrderToFront\nSet found=FindStaticID(ActivePage,' + ids.text + ',0,10000)\nIf found Is Nothing Then Err.Raise 5,"test","SIBLING_NOT_FOUND"\nMain="{""found"":" & CStr(found.StaticID) & "}"');
    const job = await run(doc, helpers + '\n' + code.replace(/^Option Explicit\s*/m, ''));
    assert.equal(job.result.value.found, ids.text);
  });
  const final = (await ok('corel_status')).result;
  assert.deepEqual(final.documents.filter(d => userIds.includes(d.document_id)).map(d => d.document_id), userIds);
} catch (error) { failure = error; }
finally {
  const cleanup = [];
  for (const document_id of owned) {
    try { await ok('corel_close_document', { ...request(), document_id, discard_changes: true }); cleanup.push(document_id); }
    catch (error) { failure ??= error; }
  }
  await writeFile(join(root, 'verification.json'), JSON.stringify({ ok: !failure, checks, cleanup_completed: cleanup.length === owned.size, error: failure?.message, root, date: new Date().toISOString() }, null, 2));
  console.log(`Recipe report: ${root}`);
  await session.close();
}
if (failure) throw failure;
