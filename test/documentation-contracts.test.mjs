import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

test('current reference teaches direct paths and replacement without an executable permission policy', async () => {
  const skill = await readFile(new URL('../skills/coreldraw-automation/SKILL.md', import.meta.url), 'utf8');
  assert.doesNotMatch(skill, /disabled_tools|writeToolPolicy|corel_status\.tool_policy/);
  assert.match(skill, /абсолютн.*пут/iu);
  assert.match(skill, /автоматическ.*замен/iu);
  const guide = await readFile(new URL('../skills/coreldraw-automation/references/tools.md', import.meta.url), 'utf8');
  assert.doesNotMatch(guide, /disabled_tools|writeToolPolicy|@output\//);
  assert.match(guide, /corel_export_diagnostics/);
  assert.match(guide, /overwrite/);
});
