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

test('api-v27.md native-fills rows reproduce the pinned CLR signatures exactly', async () => {
  const api = await readFile(new URL('../skills/coreldraw-automation/references/api-v27.md', import.meta.url), 'utf8');
  const fixture = JSON.parse(await readFile(new URL('../test/fixtures/native-fills-describe-api.json', import.meta.url), 'utf8'));
  const reflected = new Map(fixture.signatures.map(entry => [`${entry.interface}.${entry.member}`, entry]));
  // Строки таблицы: | `Interface.ReturnType Member(ParamTypes)` | [Источник](sdk_url) |
  const documented = new Map();
  for (const row of api.matchAll(/^\| `([A-Za-z]\w*)\.([^`]+)` \| \[Источник\]\(([^)]+)\) \|\r?$/gm)) {
    const member = row[2].match(/^(\S+)\s+([A-Za-z]\w*)\((.*)\)$/);
    assert.ok(member, `api-v27.md: нераспознанная сигнатура ${row[2]}`);
    documented.set(`${row[1]}.${member[2]}`, { signature: row[2], sdk_url: row[3] });
  }
  // Семь строк Task 8: заливки, прозрачность, тень. Пин — имя члена, тип возврата и типы параметров.
  for (const key of [
    'IVGFill.ApplyFountainFill',
    'IVGFountainColors.Add',
    'IVGTransparency.ApplyUniformTransparency',
    'IVGTransparency.ApplyFountainTransparency',
    'IVGTransparency.ApplyNoTransparency',
    'IVGShape.CreateDropShadow',
    'IVGShape.ClearEffect',
  ]) {
    const entry = reflected.get(key);
    assert.ok(entry, `фикстура не отражает ${key}`);
    const row = documented.get(key);
    assert.ok(row, `api-v27.md: нет строки для ${key}`);
    assert.equal(row.signature, entry.clr_signature,
      `${key}: строка api-v27.md обязана дословно повторять CLR-сигнатуру из фикстуры (член, тип возврата, типы параметров)`);
    assert.equal(row.sdk_url, entry.sdk_url, `${key}: строка api-v27.md обязана ссылаться на sdk_url из фикстуры`);
  }
});
