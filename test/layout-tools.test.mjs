import test from 'node:test';
import assert from 'node:assert/strict';
import { toolDefinitions } from '../dist/tools.js';
const binding = { request_id: 'once', document_id: 'exact', page_index: 1, static_ids: [1, 2] };
const examples = {
  corel_align_shapes: { ...binding, axis: 'x', action: 'align', align: 'center', reference: 'page' },
  corel_grid_duplicate: { ...binding, rows: 2, columns: 3, gap_x_mm: 2, gap_y_mm: 3, anchor_x_mm: 10, anchor_y_mm: 10 },
  corel_layout_shapes: { ...binding, margin_left_mm: 5, margin_right_mm: 5, margin_top_mm: 5, margin_bottom_mm: 5, gap_x_mm: 2, gap_y_mm: 3 },
};
for (const [name, args] of Object.entries(examples)) test(`${name} requires explicit binding and remains a mutation including dry_run`, () => {
  const definition = toolDefinitions[name]; assert.ok(definition, `Missing typed tool ${name}`);
  assert.equal(definition.readOnly, false); assert.equal(definition.schema.safeParse(args).success, true);
  for (const field of ['request_id','document_id','page_index','static_ids']) { const bad = { ...args }; delete bad[field]; assert.equal(definition.schema.safeParse(bad).success, false); }
  assert.equal(definition.schema.safeParse({ ...args, arbitrary_method: 'Move' }).success, false);
});
test('revision, reference and copy limits are strict', () => {
  for (const name of Object.keys(examples)) assert.ok(toolDefinitions[name], name);
  const check = (name, args) => toolDefinitions[name].schema.safeParse({ ...examples[name], ...args }).success;
  assert.equal(check('corel_align_shapes', { reference: 'shape' }), false);
  assert.equal(check('corel_align_shapes', { reference_id: 3 }), false);
  assert.equal(check('corel_align_shapes', { reference: 'shape', reference_id: 3 }), true);
  assert.equal(check('corel_grid_duplicate', { rows: 1000, columns: 2 }), false);
  assert.equal(check('corel_grid_duplicate', { gap_x_mm: -1 }), false);
  const layout = toolDefinitions.corel_layout_shapes.schema;
  assert.equal(layout.parse(examples.corel_layout_shapes).dry_run, true);
  assert.equal(check('corel_layout_shapes', { dry_run: false }), false);
  assert.equal(check('corel_layout_shapes', { dry_run: false, expected_revision: 'a'.repeat(64) }), true);
  assert.equal(check('corel_layout_shapes', { expected_revision: 'a'.repeat(64) }), false);
});
