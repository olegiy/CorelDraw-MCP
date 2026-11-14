import test from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
const directory = new URL('../skills/coreldraw-automation/examples/', import.meta.url);

test('all recipe assets are bounded data and contain no executable module source', async () => {
  const files = await readdir(directory); assert.equal(files.length, 12);
  for (const file of files) {
    assert(file.endsWith('.json'));
    const bytes = await readFile(new URL(file, directory)); assert(bytes.length <= 32768);
    const value = JSON.parse(bytes.toString('utf8'));
    assert(value.steps.length > 0 && value.steps.length <= 6);
    assert(value.steps.every(step => typeof step.tool === 'string' && !Object.hasOwn(step.arguments, 'code')));
  }
});

test('recipe reference links every validated scenario and warns that example identities must be replaced', async () => {
  const text = await readFile(new URL('../skills/coreldraw-automation/references/recipes.md', import.meta.url), 'utf8');
  const contract = await readFile(new URL('../skills/coreldraw-automation/references/recipe-contract.md', import.meta.url), 'utf8');
  for (const name of await readdir(directory)) assert(text.includes(name));
  for (const term of ['StaticID', 'request_id', 'revision', 'cleanup.completed', 'предуслов']) assert(contract.includes(term));
});
