import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const read = (path) => readFile(join(root, path), 'utf8');

test('recipe contract documents the stable Main JSON entry point and target identity', async () => {
  const contract = await read('skills/coreldraw-automation/references/recipe-contract.md');
  assert.match(contract, /Main\(\) As String/);
  assert.match(contract, /JSON/);
  assert.match(contract, /document_id/);
  assert.match(contract, /StaticID/);
  assert.match(contract, /locale|локал/i);
  assert.match(contract, /Eval|Execute|произвольн/i);
});

test('shared VBA helpers expose bounded JSON, units, target lookup, and structured errors', async () => {
  const helper = await read('skills/coreldraw-automation/examples/M_CommonHelpers.bas');
  assert.match(helper, /Option Explicit/);
  for (const name of ['JsonNumber', 'JsonEscape', 'MmToDocumentUnits', 'FindStaticID', 'RecipeError'])
    assert.match(helper, new RegExp(`(?:Function|Sub)\\s+${name}\\b`));
  assert.doesNotMatch(helper, /\b(?:Eval|Execute)\s*\(/i);
  assert.match(helper, /StaticID/);
  assert.match(helper, /cdrMillimeter/);
  assert.match(helper, /error_code/);
});

test('VBA and skill references describe recipes as examples run through corel_execute_vba', async () => {
  const [vba, skill] = await Promise.all([
    read('skills/coreldraw-automation/references/vba.md'),
    read('skills/coreldraw-automation/SKILL.md'),
  ]);
  for (const text of [vba, skill]) {
    assert.match(text, /corel_execute_vba/);
    assert.match(text, /recipe|рецепт/i);
    assert.match(text, /предуслов|precondition/i);
    assert.match(text, /cleanup|восстанов/i);
  }
});

test('recipe assembly instructions keep Option Explicit at module scope', async () => {
  const recipes = await read('skills/coreldraw-automation/references/recipes.md');
  assert.match(recipes, /не объединяйте|один модуль|Option Explicit/i);
});
