import test from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const examples = join(root, 'skills/coreldraw-automation/examples');
const recipeNames = [
  'M_TextUnicode.bas',
  'M_FillOutline.bas',
  'M_DuplicateOffset.bas',
  'M_LayerGroup.bas',
  'M_BooleanShapes.bas',
  'M_CurveNodes.bas',
];

test('six documented recipes have bounded safe source contracts', async () => {
  const recipeFiles = await readdir(examples);
  assert.deepEqual(recipeNames.filter((name) => recipeFiles.includes(name)), recipeNames);
  for (const name of recipeNames) {
    const source = await readFile(join(examples, name), 'utf8');
    assert.match(source, /Option Explicit/);
    assert.match(source, /Public Function Main\(\) As String/);
    assert.match(source, /document_id|ActiveDocument|Documents\.Count/);
    assert.match(source, /StaticID/);
    assert.match(source, /error_code|Err\.Raise|RecipeError/);
    assert.doesNotMatch(source, /\b(?:Eval|Execute|Shell|Application\.Quit)\b/i);
    assert.doesNotMatch(source, /Open\s+.+\s+For\s+/i);
    assert.doesNotMatch(source, /CreateObject\s*\(/i);
  }
});

test('recipe reference lists stable result fields and explicit preconditions', async () => {
  const recipes = await readFile(join(root, 'skills/coreldraw-automation/references/recipes.md'), 'utf8');
  for (const name of recipeNames) assert.match(recipes, new RegExp(name.replace('.', '\\.'), 'i'));
  for (const term of ['провер', 'StaticID', 'empty|пуст', 'результ'])
    assert.match(recipes, new RegExp(term, 'i'));
});
