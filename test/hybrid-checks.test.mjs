import assert from 'node:assert/strict';
import test from 'node:test';
import { verifyTranslation, verifyCreatedBatch } from './hybrid-checks.mjs';

const source = { static_id: 17, type: 1, layer: 'Слой', rotation: 0,
  bounds_mm: { left: 10, bottom: 20, width: 12, height: 8 },
  appearance: { fill_color: { model: 'rgb', r: 80, g: 120, b: 180 }, outline_type: 0 } };
const translated = { ...source, bounds_mm: { left: 13, bottom: 18, width: 12, height: 8 } };

test('translation oracle checks both axes and preserves geometry, identity and appearance', () => {
  verifyTranslation([source], [translated], [17], 3, -2);
  verifyTranslation([translated], [source], [17], -3, 2);
  for (const broken of [
    { ...translated, static_id: 18 }, { ...translated, type: 2 },
    { ...translated, layer: 'Другой' }, { ...translated, rotation: 90 },
    { ...translated, appearance: { ...source.appearance, outline_type: 1 } },
    ...['left', 'bottom', 'width', 'height'].map(key => ({ ...translated,
      bounds_mm: { ...translated.bounds_mm, [key]: translated.bounds_mm[key] + 1 } })),
  ]) assert.throws(() => verifyTranslation([source], [broken], [17], 3, -2));
  assert.throws(() => verifyTranslation([source], [translated, translated], [17], 3, -2));
});

test('translation oracle rejects duplicate source identities and preserves untargeted shapes', () => {
  assert.throws(() => verifyTranslation([source, source], [translated], [17], 3, -2));
  const untouched = { ...source, static_id: 19, text: 'Контроль' };
  verifyTranslation([source, untouched], [translated, untouched], [17], 3, -2);
  assert.throws(() => verifyTranslation([source, untouched], [translated, { ...untouched, text: 'Изменён' }], [17], 3, -2));
  assert.throws(() => verifyTranslation([source], [{ ...translated, bounds_mm: { ...translated.bounds_mm, left: NaN } }], [17], 3, -2));
});

test('batch oracle rejects missing, duplicated and reordered item-to-ID mappings', () => {
  const input = [{ item_key: 'rectangle', kind: 'rectangle' }, { item_key: 'ellipse', kind: 'ellipse' }];
  const reply = { created_static_ids: [17, 18], items: [
    { item_key: 'rectangle', static_id: 17 }, { item_key: 'ellipse', static_id: 18 },
  ] };
  verifyCreatedBatch(input, reply);
  for (const broken of [
    { ...reply, created_static_ids: [17, 17] }, { ...reply, items: reply.items.slice(0, 1) },
    { ...reply, items: [...reply.items].reverse() },
    { ...reply, items: [reply.items[0], { item_key: 'ellipse', static_id: 17 }] },
  ]) assert.throws(() => verifyCreatedBatch(input, broken));
});
