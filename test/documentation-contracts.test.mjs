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

test('native fills documentation teaches fountain, transparency and drop shadow with their observed limits', async () => {
  const read = relative => readFile(new URL(`../${relative}`, import.meta.url), 'utf8');
  const [skill, operations, pitfalls, recipes, api, architecture, workerReadme] = await Promise.all([
    'skills/coreldraw-automation/SKILL.md', 'skills/coreldraw-automation/references/operations.md',
    'skills/coreldraw-automation/references/pitfalls.md', 'skills/coreldraw-automation/references/recipes.md',
    'skills/coreldraw-automation/references/api-v27.md', 'docs/architecture.md', 'worker/README.md',
  ].map(read));
  // Скилл называет новые операции, наблюдаемый read-back и id_mappings тени.
  assert.match(skill, /corel_set_transparency/);
  assert.match(skill, /corel_apply_drop_shadow/);
  assert.match(skill, /fountain/iu);
  assert.match(skill, /id_mappings/);
  assert.match(skill, /наблюдаем/iu);
  // operations: контракт операций, observed-факты и барьеры.
  assert.match(operations, /BATCH_FILL_UNSUPPORTED/);
  assert.match(operations, /0\/100/);
  assert.match(operations, /1–99/);
  assert.match(operations, /EdgePad/);
  assert.match(operations, /blend_type=custom/);
  assert.match(operations, /AppliedTo/);
  assert.match(operations, /Freeze/);
  assert.match(operations, /id_mappings/);
  assert.match(operations, /ClearEffect/);
  assert.match(operations, /Mesh/iu);
  assert.match(operations, /1\/2\/3\/4/);
  // pitfalls: наблюдаемые факты T4–T6 и ограничения.
  assert.match(pitfalls, /EdgePad/);
  assert.match(pitfalls, /0→15/);
  assert.match(pitfalls, /20→29/);
  assert.match(pitfalls, /blend_type=custom/);
  assert.match(pitfalls, /256/);
  assert.match(pitfalls, /с нуля/);
  assert.match(pitfalls, /AppliedTo/);
  assert.match(pitfalls, /Freeze/);
  assert.match(pitfalls, /id_mappings/);
  assert.match(pitfalls, /ClearEffect/);
  assert.match(pitfalls, /non-live/);
  assert.match(pitfalls, /узл.*групп/iu);
  assert.match(pitfalls, /cdrMeshFillShape/);
  assert.match(pitfalls, /BATCH_FILL_UNSUPPORTED/);
  // recipes: новые строки каталога.
  assert.match(recipes, /fountain-fill\.json/);
  assert.match(recipes, /transparency-shadow\.json/);
  // api-v27: проверенные сигнатуры новых операций.
  assert.match(api, /ApplyFountainFill/);
  assert.match(api, /fountaincolors\.add/iu);
  assert.match(api, /ApplyUniformTransparency/);
  assert.match(api, /ApplyFountainTransparency/);
  assert.match(api, /ApplyNoTransparency/);
  assert.match(api, /CreateDropShadow/);
  assert.match(api, /ClearEffect/);
  // architecture: C#-путь, mesh non-goal, id_mappings, observed read-back.
  assert.match(architecture, /BATCH_FILL_UNSUPPORTED/);
  assert.match(architecture, /Mesh/iu);
  assert.match(architecture, /id_mappings/);
  assert.match(architecture, /blend_type=custom/);
  assert.match(architecture, /EdgePad/);
  // worker README: новые инструменты, id_mappings, барьер и interop-прerequisite.
  assert.match(workerReadme, /corel_set_transparency/);
  assert.match(workerReadme, /corel_apply_drop_shadow/);
  assert.match(workerReadme, /id_mappings/);
  assert.match(workerReadme, /BATCH_FILL_UNSUPPORTED/);
  assert.match(workerReadme, /GenerateInteropOnly/);
});
