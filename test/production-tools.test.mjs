import test from 'node:test';
import assert from 'node:assert/strict';
import * as z from 'zod';
import { toolDefinitions } from '../dist/tools.js';

const binding = { document_id: 'exact', page_index: 1 };
const mutation = { ...binding, request_id: 'once' };
const revision = 'a'.repeat(64);
const examples = {
  corel_inspect_curve: { ...binding, static_id: 1 },
  corel_create_curve: { ...mutation, layer_id: 'layer', subpaths: [{ start: { x_mm: 0, y_mm: 0 }, segments: [{ kind: 'cubic', to: { x_mm: 10, y_mm: 10 }, control1: { x_mm: 0, y_mm: 5 }, control2: { x_mm: 10, y_mm: 5 } }], closed: false }] },
  corel_edit_curve: { ...mutation, static_id: 1, expected_revision: revision, action: 'insert_node', subpath_index: 1, segment_index: 1, t: 0.5 },
  corel_edit_text: { ...mutation, static_ids: [1], action: 'replace_literal', old: 'Ёж', new: 'Текст', case_sensitive: true },
  corel_find_text: { ...binding, query: 'Ёж', case_sensitive: false },
  corel_preflight: { ...binding, static_ids: [1], checks: [{ kind: 'outline_width', expected_mm: 0.2, tolerance_mm: 0.01 }, { kind: 'text_present' }] },
};
for (const [name, args] of Object.entries(examples)) {
  test(`${name} has a strict serializable schema and exact target binding`, () => {
    const definition = toolDefinitions[name]; assert.ok(definition, `Missing typed tool ${name}`);
    assert.equal(definition.schema.safeParse(args).success, true);
    assert.equal(definition.schema.safeParse({ ...args, expression: 'anything' }).success, false);
    for (const field of ['document_id', 'page_index', ...(args.request_id ? ['request_id'] : [])]) {
      const bad = { ...args }; delete bad[field]; assert.equal(definition.schema.safeParse(bad).success, false, field);
    }
    const json = z.toJSONSchema(definition.schema, { io: 'input' }); assert.equal(json.type, 'object');
    assert.equal(definition.readOnly, ['corel_inspect_curve', 'corel_find_text', 'corel_preflight'].includes(name));
  });
}
test('curve variants, revision, range and total node limits reject before dispatch', () => {
  for (const name of Object.keys(examples)) assert.ok(toolDefinitions[name], name);
  const reject = (name, changes) => assert.equal(toolDefinitions[name].schema.safeParse({ ...examples[name], ...changes }).success, false);
  reject('corel_edit_curve', { expected_revision: 'stale' });
  reject('corel_edit_curve', { t: 0 }); reject('corel_edit_curve', { t: 1 });
  reject('corel_edit_curve', { subpath_index: -1 }); reject('corel_edit_curve', { segment_index: 0 });
  reject('corel_edit_curve', { action: 'delete_node', node_index: 1 }); // insert-only fields are forbidden
  const path = examples.corel_create_curve.subpaths[0];
  reject('corel_create_curve', { subpaths: [{ ...path, segments: Array(10000).fill(path.segments[0]) }] });
  reject('corel_create_curve', { subpaths: [{ ...path, closed: true }] });
  reject('corel_edit_text', { new: '🦔' }); reject('corel_edit_text', { old: '' });
  reject('corel_edit_text', { regex: true });
  reject('corel_preflight', { checks: [{ kind: 'outline_width', expected_mm: 0.2 }] });
  reject('corel_preflight', { checks: [{ kind: 'dimensions', min_width_mm: 20, max_width_mm: 10, min_height_mm: 0, max_height_mm: 100, tolerance_mm: 0.01 }] });
  reject('corel_preflight', { checks: [{ kind: 'auto_fix' }] });
});
