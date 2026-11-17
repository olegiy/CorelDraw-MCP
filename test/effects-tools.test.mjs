import test from 'node:test';
import assert from 'node:assert/strict';
import * as z from 'zod';
import { toolDefinitions } from '../dist/tools.js';

const common = { request_id: 'once', document_id: 'exact', page_index: 1 };
const binding = { ...common, static_ids: [1, 2], kind: 'uniform', value: 40, applied_to: 'fill', merge_mode: 'normal' };

test('corel_set_transparency: explicit binding, strict fields and finite bounds', () => {
  const definition = toolDefinitions.corel_set_transparency;
  assert.ok(definition, 'Missing typed tool corel_set_transparency');
  assert.equal(definition.readOnly, false);
  assert.equal(definition.backend, 'csharp');
  assert.equal(definition.schema.parse(binding).deadline_seconds, 600);
  for (const field of Object.keys(common)) {
    const invalid = { ...binding }; delete invalid[field];
    assert.equal(definition.schema.safeParse(invalid).success, false, field);
  }
  for (const field of ['static_ids', 'kind', 'value', 'applied_to', 'merge_mode']) {
    const invalid = { ...binding }; delete invalid[field];
    assert.equal(definition.schema.safeParse(invalid).success, false, field);
  }
  assert.equal(definition.schema.safeParse({ ...binding, expression: 'Application.Quit' }).success, false);
  assert.equal(definition.schema.safeParse({ ...binding, static_ids: [1, 1] }).success, false);
  assert.equal(definition.schema.safeParse({ ...binding, static_ids: [] }).success, false);
  assert.doesNotThrow(() => z.toJSONSchema(definition.schema, { io: 'input' }));
});

test('valid uniform, fountain and none transparency variants pass through unchanged', () => {
  const schema = toolDefinitions.corel_set_transparency.schema;
  const variants = [
    { ...binding },
    { ...common, static_ids: [1, 2], kind: 'none', applied_to: 'fill', merge_mode: 'normal' },
    { ...common, static_ids: [1], kind: 'uniform', value: 0, applied_to: 'fill_and_outline', merge_mode: 'behind', freeze: true },
    { ...common, static_ids: [1], kind: 'fountain', start: 0, end: 100, type: 'linear', applied_to: 'outline', merge_mode: 'multiply' },
    { ...common, static_ids: [1], kind: 'fountain', start: 25, end: 75, type: 'radial',
      center_offset_x_percent: 20, center_offset_y_percent: -30, applied_to: 'fill', merge_mode: 'screen' },
    { ...common, static_ids: [1], kind: 'fountain', start: 0, end: 100, type: 'square', angle_deg: -45,
      applied_to: 'fill', merge_mode: 'overlay', freeze: true },
    { ...common, static_ids: [1], kind: 'fountain', start: 0, end: 100, type: 'conical', angle_deg: 360,
      center_offset_x_percent: -100, center_offset_y_percent: 100, applied_to: 'fill', merge_mode: 'normal' },
  ];
  for (const args of variants) {
    const expected = { ...args };
    delete expected.request_id; delete expected.document_id; delete expected.page_index;
    const parsed = schema.parse(args);
    for (const [key, value] of Object.entries(expected)) assert.deepEqual(parsed[key], value, key);
    for (const key of ['value', 'start', 'end', 'type', 'angle_deg', 'center_offset_x_percent', 'center_offset_y_percent', 'freeze'])
      assert.ok(!(key in parsed) || key in expected, key);
  }
});

test('per-kind refines reject misplaced, missing and out-of-range fields', () => {
  const schema = toolDefinitions.corel_set_transparency.schema;
  const reject = changes => assert.equal(schema.safeParse({ ...binding, ...changes }).success, false, JSON.stringify(changes));
  // uniform: value required, bounded integer; fountain fields must not leak
  reject({ value: 101 });
  reject({ value: -1 });
  reject({ value: 40.5 });
  reject({ value: undefined });
  reject({ start: 0 });
  reject({ end: 100 });
  reject({ type: 'linear' });
  reject({ angle_deg: 45 });
  reject({ center_offset_x_percent: 10 });
  // none: no transparency parameters are accepted (freeze only applies after a new transparency)
  reject({ kind: 'none', value: 40 });
  reject({ kind: 'none', start: 0, end: 100, type: 'linear' });
  reject({ kind: 'none', freeze: true });
  // fountain: start/end/type required and bounded; value forbidden; angle not for radial; center not for linear
  reject({ kind: 'fountain', value: 40, start: 0, end: 100, type: 'linear' });
  reject({ kind: 'fountain', start: 0, end: 100, type: 'linear', value: undefined, start2: 0 });
  reject({ kind: 'fountain', end: 100, type: 'linear' });
  reject({ kind: 'fountain', start: 0, type: 'linear' });
  reject({ kind: 'fountain', start: 0, end: 100 });
  reject({ kind: 'fountain', start: 101, end: 100, type: 'linear' });
  reject({ kind: 'fountain', start: 0, end: -1, type: 'linear' });
  reject({ kind: 'fountain', start: 0, end: 100, type: 'circular' });
  reject({ kind: 'fountain', start: 0, end: 100, type: 'radial', angle_deg: 45 });
  reject({ kind: 'fountain', start: 0, end: 100, type: 'linear', center_offset_x_percent: 10 });
  reject({ kind: 'fountain', start: 0, end: 100, type: 'linear', center_offset_y_percent: -10 });
  reject({ kind: 'fountain', start: 0, end: 100, type: 'conical', angle_deg: 360.5 });
  reject({ kind: 'fountain', start: 0, end: 100, type: 'radial', center_offset_x_percent: -100.5 });
  reject({ kind: 'fountain', start: 0, end: 100, type: 'square', center_offset_y_percent: 100.5 });
});

test('merge_mode and applied_to accept only whitelisted names', () => {
  const schema = toolDefinitions.corel_set_transparency.schema;
  for (const merge_mode of ['normal', 'multiply', 'screen', 'overlay', 'behind']) {
    assert.deepEqual(schema.parse({ ...binding, merge_mode }).merge_mode, merge_mode, merge_mode);
  }
  for (const merge_mode of ['xor', 'normal ', 'NORMAL', 'dodge', '']) {
    assert.equal(schema.safeParse({ ...binding, merge_mode }).success, false, JSON.stringify(merge_mode));
  }
  for (const applied_to of ['fill', 'outline', 'fill_and_outline']) {
    assert.deepEqual(schema.parse({ ...binding, applied_to }).applied_to, applied_to, applied_to);
  }
  for (const applied_to of ['both', 'fills', 'fill ', '']) {
    assert.equal(schema.safeParse({ ...binding, applied_to }).success, false, JSON.stringify(applied_to));
  }
  for (const kind of ['pattern', 'texture', 'uniform ', '']) {
    assert.equal(schema.safeParse({ ...binding, kind }).success, false, JSON.stringify(kind));
  }
});
