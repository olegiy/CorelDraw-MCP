import test from 'node:test';
import assert from 'node:assert/strict';
import * as z from 'zod';
import { toolDefinitions } from '../dist/tools.js';

const common = { request_id: 'once', document_id: 'exact', page_index: 1 };
export const examples = {
  corel_create_shapes: { layer_id: 'layer', items: [{ kind: 'rectangle', x_mm: 0, y_mm: 0, width_mm: 10, height_mm: 20 }, { kind: 'ellipse', x_mm: 20, y_mm: 30, width_mm: 2, height_mm: 3 }, { kind: 'line', x1_mm: 0, y1_mm: 0, x2_mm: 10, y2_mm: 10 }] },
  corel_transform_shapes: { static_ids: [1], action: 'scale', sx: 2, sy: 0.5, anchor_x_mm: 0, anchor_y_mm: 0 },
  corel_duplicate_shapes: { static_ids: [1], copies: 2, step_x_mm: 10, step_y_mm: 0 },
  corel_style_shapes: { static_ids: [1], fill: { kind: 'uniform', color: { model: 'rgb', r: 255, g: 0, b: 0 } }, outline: { kind: 'uniform', width_mm: 0.2, color: { model: 'cmyk', c: 0, m: 0, y: 0, k: 100 } } },
  corel_manage_layers: { action: 'create', page_id: 'page', name: 'Резка' },
  corel_group_shapes: { action: 'group', static_ids: [1, 2] },
  corel_boolean_shapes: { action: 'trim', left_id: 1, right_id: 2, keep_left: true, keep_right: false },
  corel_create_text: { kind: 'artistic', layer_id: 'layer', text: 'Ёж\nТекст', x_mm: 10, y_mm: 20, font_family: 'Arial', font_size_pt: 12 },
  corel_convert_to_curves: { static_ids: [1] },
};

for (const [name, example] of Object.entries(examples)) {
  test(`${name}: explicit binding, strict fields and finite bounds`, () => {
    const definition = toolDefinitions[name];
    assert.ok(definition, `Missing typed tool ${name}`);
    const schema = definition.schema;
    assert.equal(definition.readOnly, false);
    assert.equal(definition.backend, 'csharp');
    assert.equal(schema.parse({ ...common, ...example }).deadline_seconds, 600);
    for (const field of Object.keys(common)) {
      const invalid = { ...common, ...example }; delete invalid[field];
      assert.equal(schema.safeParse(invalid).success, false, field);
    }
    assert.equal(schema.safeParse({ ...common, ...example, expression: 'Application.Quit' }).success, false);
    assert.doesNotThrow(() => z.toJSONSchema(schema, { io: 'input' }));
  });
}

test('nested variants reject wrong fields, nulls, unsupported text and excess output', () => {
  const reject = (name, changes) => {
    assert.ok(toolDefinitions[name], `Missing typed tool ${name}`);
    assert.equal(toolDefinitions[name].schema.safeParse({ ...common, ...examples[name], ...changes }).success, false, JSON.stringify(changes));
  };
  reject('corel_create_shapes', { items: [{ kind: 'rectangle', x_mm: 0, y_mm: 0, width_mm: 0, height_mm: 1 }] });
  reject('corel_create_shapes', { items: [{ kind: 'line', x1_mm: 0, y1_mm: 0, x2_mm: Infinity, y2_mm: 1 }] });
  reject('corel_create_shapes', { items: Array(101).fill(examples.corel_create_shapes.items[0]) });
  reject('corel_transform_shapes', { sx: -1 });
  reject('corel_transform_shapes', { action: 'move', dx_mm: 1, dy_mm: 0 }); // fields from scale must not leak
  reject('corel_duplicate_shapes', { static_ids: [1, 2], copies: 501 });
  reject('corel_duplicate_shapes', { static_ids: [1, 1] });
  reject('corel_style_shapes', { fill: { kind: 'uniform', color: { model: 'rgb', r: 256, g: 0, b: 0 } } });
  reject('corel_style_shapes', { fill: undefined, outline: undefined });
  reject('corel_style_shapes', { fill: null });
  reject('corel_manage_layers', { action: 'delete' });
  reject('corel_group_shapes', { action: 'ungroup', static_ids: [1, 2] });
  reject('corel_boolean_shapes', { right_id: 1 });
  reject('corel_boolean_shapes', { keep_right: undefined });
  for (const text of ['🦔', '\ud800', '\udc00', '\0', 'a'.repeat(100001)]) reject('corel_create_text', { text });
  reject('corel_create_text', { font_size_pt: 0 });
  reject('corel_convert_to_curves', { static_ids: [] });
});
