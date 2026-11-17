import test from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { toolDefinitions } from '../dist/tools.js';

const directory = fileURLToPath(new URL('../skills/coreldraw-automation/examples/', import.meta.url));
for (const file of readdirSync(directory)) test(`typed recipe ${file} has strict executable contracts and explicit preconditions`, () => {
  assert(file.endsWith('.json'), 'Примеры должны содержать только готовые JSON-вызовы.');
  const recipe = JSON.parse(readFileSync(join(directory, file), 'utf8'));
  assert.equal(recipe.schema_version, 1); assert(recipe.preconditions.length && recipe.limitations.length);
  assert.deepEqual(recipe.postchecks, ['corel_inspect_document', 'corel_preview']);
  for (const step of recipe.steps) {
    assert(!['corel_execute_vba', 'corel_build_vba'].includes(step.tool));
    const definition = toolDefinitions[step.tool]; assert(definition, step.tool);
    const parsed = definition.schema.safeParse(step.arguments);
    assert(parsed.success, `${file}/${step.tool}: ${parsed.error?.message}`);
  }
});
test('recipe catalog documents every example and pins the native-fills recipes', () => {
  const catalog = readFileSync(join(directory, '..', 'references', 'recipes.md'), 'utf8');
  const files = readdirSync(directory).filter(name => name.endsWith('.json'));
  const documented = [...catalog.matchAll(/\]\(\.\.\/examples\/([a-z0-9-]+\.json)\)/g)].map(match => match[1]);
  assert.deepEqual([...files].sort(), [...documented].sort(), 'recipes.md и каталог examples/ должны перечислять одни и те же рецепты');
  assert(files.includes('fountain-fill.json'), 'отсутствует рецепт fountain-fill.json');
  assert(files.includes('transparency-shadow.json'), 'отсутствует рецепт transparency-shadow.json');
});
