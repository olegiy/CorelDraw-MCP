import assert from 'node:assert/strict';

export const near = (actual, expected) => assert(Number.isFinite(actual) && Math.abs(actual - expected) <= .001, `${actual} != ${expected}`);

export function verifyTranslation(before, after, ids, dx, dy) {
  const original = new Map(before.map(shape => [shape.static_id, shape]));
  const actual = new Map(after.map(shape => [shape.static_id, shape]));
  assert.equal(original.size, before.length);
  assert.equal(actual.size, after.length);
  assert.deepEqual([...actual.keys()].sort((a, b) => a - b), [...original.keys()].sort((a, b) => a - b));
  assert.equal(new Set(ids).size, ids.length);
  for (const id of ids) {
    const source = original.get(id), shape = actual.get(id);
    assert(source && shape, `Отсутствует StaticID ${id}`);
    near(shape.bounds_mm.left, source.bounds_mm.left + dx);
    near(shape.bounds_mm.bottom, source.bounds_mm.bottom + dy);
    for (const key of ['width', 'height']) near(shape.bounds_mm[key], source.bounds_mm[key]);
    near(shape.rotation, source.rotation);
    for (const key of ['type', 'layer', 'appearance', 'text']) assert.deepEqual(shape[key], source[key]);
  }
  for (const [id, source] of original) if (!ids.includes(id)) assert.deepEqual(actual.get(id), source);
}

export function verifyCreatedBatch(input, result) {
  assert.equal(result.created_static_ids.length, input.length);
  assert.equal(new Set(result.created_static_ids).size, input.length);
  assert.equal(result.items.length, input.length);
  result.items.forEach((item, index) => {
    assert.equal(item.item_key, input[index].item_key);
    assert.equal(item.static_id, result.created_static_ids[index]);
  });
}
