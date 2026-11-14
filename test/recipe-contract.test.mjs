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
