import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { measureSnapshot } from '../dist/geometry.js';

// Input is the retained output of the compiled LiveGeometryExportTests fixture.
// No CorelDRAW calls, document setup, VBA or mutation occur in this check.
const result = measureSnapshot(JSON.parse(readFileSync(process.argv[2], 'utf8')), 0.01);
assert.equal(result.objects.length, 6);
for (const [index, expected] of [[0, 20], [1, 10], [2, 10], [3, 10], [5, 10.2]])
  assert(Math.abs(result.objects[index].min_width_mm - expected) <= 0.02,
    `Фигура ${index}: ожидалась минимальная ширина ${expected}, получена ${result.objects[index].min_width_mm}`);
assert(result.objects[4].min_width_mm > 0);
console.log(JSON.stringify({ checked: 6, min_widths_mm: result.objects.map(item => item.min_width_mm) }));
