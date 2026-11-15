import test from 'node:test';
import assert from 'node:assert/strict';
import { parseBenchmarkOptions, createBenchmarkBudget } from '../scripts/bench-hybrid.mjs';

test('explicit scenario budget preserves normal defaults and accepts a bounded long run', () => {
  assert.equal(parseBenchmarkOptions([]).budgetMs, 300000);
  assert.deepEqual(parseBenchmarkOptions(['tiles1000', '3', '--budget-ms', '1800000']),
    { cases: ['tiles1000'], samples: 3, budgetMs: 1800000 });
  assert.deepEqual(parseBenchmarkOptions(['text20,text100', '5']),
    { cases: ['text20', 'text100'], samples: 5, budgetMs: 300000 });
});

test('invalid options and duplicate or empty cases cannot reach execution', () => {
  for (const value of ['0', '1800001', 'NaN', 'Infinity', '1.2', '-1', '1e3', ''])
    assert.throws(() => parseBenchmarkOptions(['tiles1000', '3', '--budget-ms', value]));
  for (const args of [[''], ['tiles20,tiles20'], ['tiles20,'], ['unknown'], ['tiles1000', '3', '--budget-ms'],
    ['tiles1000', '3', '--budget-ms', '600000', '--budget-ms', '600000'], ['tiles1000', '3', '--unknown', '600000'],
    ['tiles1000', '0'], ['tiles1000', '1.5'], ['tiles1000', '6']])
    assert.throws(() => parseBenchmarkOptions(args));
});

test('cooperative budget rejects a late completed operation without replay or interrupting it', async () => {
  let now = 0, mutations = 0;
  const check = createBenchmarkBudget(10, () => now);
  check();
  await (async () => { mutations++; now = 11; })();
  assert.throws(check, /бюджет/);
  assert.equal(mutations, 1);
});
