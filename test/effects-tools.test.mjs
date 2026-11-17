import test from 'node:test';
import assert from 'node:assert/strict';
import * as z from 'zod';
import { toolDefinitions } from '../dist/tools.js';

const common = { request_id: 'once', document_id: 'exact', page_index: 1 };
const binding = { ...common, static_ids: [1, 2], kind: 'uniform', value: 40, applied_to: 'fill', merge_mode: 'normal' };
const shadowBinding = {
  ...common, static_ids: [1, 2], kind: 'shadow', type: 'flat', opacity: 70, feather: 50, offset_x_mm: 5, offset_y_mm: 5,
  color: { model: 'rgb', r: 0, g: 0, b: 0 }, feather_type: 'inside', feather_edge: 'gaussian', merge_mode: 'normal',
};

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
    { ...common, static_ids: [1], kind: 'none' },
    { ...common, static_ids: [1], kind: 'none', merge_mode: 'behind' },
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

test('applied_to and merge_mode are optional only for kind none and stay whitelisted', () => {
  const schema = toolDefinitions.corel_set_transparency.schema;
  // kind none needs no envelope fields: defaults fill/normal are applied by the worker and ignored on removal.
  const bare = schema.parse({ ...common, static_ids: [1], kind: 'none' });
  assert.equal(bare.kind, 'none');
  assert.ok(!('applied_to' in bare) && !('merge_mode' in bare), 'absent envelope fields must stay absent');
  // Provided values still pass through (the worker ignores them when removing).
  const ignored = schema.parse({ ...common, static_ids: [1], kind: 'none', applied_to: 'outline', merge_mode: 'behind' });
  assert.equal(ignored.applied_to, 'outline');
  assert.equal(ignored.merge_mode, 'behind');
  // Apply kinds keep both fields required.
  assert.equal(schema.safeParse({ ...common, static_ids: [1], kind: 'uniform', value: 40 }).success, false);
  assert.equal(schema.safeParse({ ...common, static_ids: [1], kind: 'uniform', value: 40, applied_to: 'fill' }).success, false);
  assert.equal(schema.safeParse({ ...common, static_ids: [1], kind: 'uniform', value: 40, merge_mode: 'normal' }).success, false);
  assert.equal(schema.safeParse({ ...common, static_ids: [1], kind: 'fountain', start: 0, end: 100, type: 'linear' }).success, false);
  // Whitelists still apply on removal.
  assert.equal(schema.safeParse({ ...common, static_ids: [1], kind: 'none', applied_to: 'both' }).success, false);
  assert.equal(schema.safeParse({ ...common, static_ids: [1], kind: 'none', merge_mode: 'xor' }).success, false);
});

test('corel_apply_drop_shadow: explicit binding, strict fields and finite bounds', () => {
  const definition = toolDefinitions.corel_apply_drop_shadow;
  assert.ok(definition, 'Missing typed tool corel_apply_drop_shadow');
  assert.equal(definition.readOnly, false);
  assert.equal(definition.backend, 'csharp');
  assert.equal(definition.schema.parse(shadowBinding).deadline_seconds, 600);
  for (const field of Object.keys(common)) {
    const invalid = { ...shadowBinding }; delete invalid[field];
    assert.equal(definition.schema.safeParse(invalid).success, false, field);
  }
  for (const field of ['static_ids', 'kind', 'type', 'opacity', 'feather', 'offset_x_mm', 'offset_y_mm', 'color',
    'feather_type', 'feather_edge', 'merge_mode']) {
    const invalid = { ...shadowBinding }; delete invalid[field];
    assert.equal(definition.schema.safeParse(invalid).success, false, field);
  }
  assert.equal(definition.schema.safeParse({ ...shadowBinding, expression: 'Application.Quit' }).success, false);
  assert.equal(definition.schema.safeParse({ ...shadowBinding, static_ids: [1, 1] }).success, false);
  assert.equal(definition.schema.safeParse({ ...shadowBinding, static_ids: [] }).success, false);
  assert.doesNotThrow(() => z.toJSONSchema(definition.schema, { io: 'input' }));
});

test('valid shadow and none drop-shadow variants pass through unchanged', () => {
  const schema = toolDefinitions.corel_apply_drop_shadow.schema;
  const variants = [
    { ...shadowBinding },
    { ...shadowBinding, type: 'bottom', fade: 30, color: { model: 'cmyk', c: 0, m: 0, y: 0, k: 100 } },
    { ...shadowBinding, type: 'top', feather_type: 'gaussian', feather_edge: 'inverse_squared', merge_mode: 'behind' },
    { ...shadowBinding, type: 'left', opacity: 100, feather: 0, offset_x_mm: -1000, offset_y_mm: 1000, fade: 0 },
    { ...shadowBinding, type: 'right', offset_x_mm: 0.25, offset_y_mm: -0.25 },
    { ...common, static_ids: [1], kind: 'none' },
    { ...common, static_ids: [1], kind: 'none', merge_mode: 'screen' },
  ];
  for (const args of variants) {
    const expected = { ...args };
    delete expected.request_id; delete expected.document_id; delete expected.page_index;
    const parsed = schema.parse(args);
    for (const [key, value] of Object.entries(expected)) assert.deepEqual(parsed[key], value, key);
    for (const key of ['type', 'opacity', 'feather', 'offset_x_mm', 'offset_y_mm', 'color', 'feather_type', 'feather_edge', 'merge_mode', 'fade'])
      assert.ok(!(key in parsed) || key in expected, key);
  }
});

test('per-kind refines reject misplaced, missing and out-of-range shadow fields', () => {
  const schema = toolDefinitions.corel_apply_drop_shadow.schema;
  const reject = changes => assert.equal(schema.safeParse({ ...shadowBinding, ...changes }).success, false, JSON.stringify(changes));
  // fade is forbidden for flat and bounded otherwise
  reject({ fade: 30 });
  reject({ fade: 0 });
  reject({ type: 'bottom', fade: 101 });
  reject({ type: 'bottom', fade: -1 });
  reject({ type: 'bottom', fade: 30.5 });
  // bounded integers and offsets
  reject({ opacity: 101 });
  reject({ opacity: -1 });
  reject({ opacity: 70.5 });
  reject({ feather: 101 });
  reject({ feather: -1 });
  reject({ feather: 50.5 });
  reject({ offset_x_mm: 1000.5 });
  reject({ offset_x_mm: -1000.5 });
  reject({ offset_y_mm: 1000.5 });
  reject({ offset_y_mm: -1000.5 });
  // color models stay bounded
  reject({ color: { model: 'rgb', r: 256, g: 0, b: 0 } });
  reject({ color: { model: 'cmyk', c: 0, m: 0, y: 0, k: 101 } });
  reject({ color: { model: 'cmyk' } });
  // kind none rejects every shadow parameter; merge_mode is the ignored envelope field
  reject({ kind: 'none', type: 'flat' });
  reject({ kind: 'none', opacity: 70 });
  reject({ kind: 'none', feather: 50 });
  reject({ kind: 'none', offset_x_mm: 5 });
  reject({ kind: 'none', offset_y_mm: 5 });
  reject({ kind: 'none', color: { model: 'rgb', r: 0, g: 0, b: 0 } });
  reject({ kind: 'none', feather_type: 'inside' });
  reject({ kind: 'none', feather_edge: 'gaussian' });
  reject({ kind: 'none', fade: 30 });
  // kind shadow requires every shadow parameter
  for (const field of ['type', 'opacity', 'feather', 'offset_x_mm', 'offset_y_mm', 'color', 'feather_type', 'feather_edge', 'merge_mode']) {
    const invalid = { ...shadowBinding }; delete invalid[field];
    assert.equal(schema.safeParse(invalid).success, false, field);
  }
});

test('drop shadow whitelists accept only known names', () => {
  const schema = toolDefinitions.corel_apply_drop_shadow.schema;
  for (const type of ['flat', 'bottom', 'top', 'left', 'right']) {
    assert.deepEqual(schema.parse({ ...shadowBinding, type, fade: type === 'flat' ? undefined : 10 }).type, type, type);
  }
  for (const type of ['middle', 'FLAT', 'flat ', '']) {
    assert.equal(schema.safeParse({ ...shadowBinding, type }).success, false, JSON.stringify(type));
  }
  for (const feather_type of ['inside', 'middle', 'outside', 'average', 'gaussian']) {
    assert.deepEqual(schema.parse({ ...shadowBinding, type: 'bottom', fade: 20, feather_type }).feather_type, feather_type, feather_type);
  }
  for (const feather_type of ['gaussian blur', 'gauss', 'blur', '']) {
    assert.equal(schema.safeParse({ ...shadowBinding, type: 'bottom', fade: 20, feather_type }).success, false, JSON.stringify(feather_type));
  }
  for (const feather_edge of ['linear', 'squared', 'flat', 'inverse_squared', 'mesa', 'gaussian']) {
    assert.deepEqual(schema.parse({ ...shadowBinding, feather_edge }).feather_edge, feather_edge, feather_edge);
  }
  for (const feather_edge of ['inverse-squared', 'flat edge', 'step', '']) {
    assert.equal(schema.safeParse({ ...shadowBinding, feather_edge }).success, false, JSON.stringify(feather_edge));
  }
  for (const merge_mode of ['normal', 'multiply', 'screen', 'overlay', 'behind']) {
    assert.deepEqual(schema.parse({ ...shadowBinding, merge_mode }).merge_mode, merge_mode, merge_mode);
  }
  for (const merge_mode of ['xor', 'dodge', 'burn', 'normal ', '']) {
    assert.equal(schema.safeParse({ ...shadowBinding, merge_mode }).success, false, JSON.stringify(merge_mode));
  }
  for (const merge_mode of ['normal', 'behind']) {
    assert.deepEqual(schema.parse({ ...common, static_ids: [1], kind: 'none', merge_mode }).merge_mode, merge_mode, merge_mode);
  }
  for (const kind of ['remove', 'delete', 'shadow ', 'none ', '']) {
    assert.equal(schema.safeParse({ ...shadowBinding, kind }).success, false, JSON.stringify(kind));
  }
});
