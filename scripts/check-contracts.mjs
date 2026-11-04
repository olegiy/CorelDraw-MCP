import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import * as z from 'zod';

const args = process.argv.slice(2);
if (args.length && (args.length !== 2 || args[0] !== '--root')) throw new Error('Usage: check-contracts.mjs [--root directory]');
const root = args.length ? resolve(args[1]) : join(dirname(fileURLToPath(import.meta.url)), '..');
const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };
try {
  const { toolDefinitions, getJobSchema, guideSchema } = await import(pathToFileURL(join(root, 'dist/tools.js')).href);
  const skill = readFileSync(join(root, 'skills/coreldraw-automation/SKILL.md'), 'utf8');
  const references = readFileSync(join(root, 'skills/coreldraw-automation/references/tools.md'), 'utf8');
  const recipes = readFileSync(join(root, 'skills/coreldraw-automation/references/recipes.md'), 'utf8');
  const descriptions = JSON.parse(references.match(/```json\r?\n([\s\S]*?)\r?\n```/)?.[1] ?? '{}');
  const block = references.match(/```jsonl\r?\n([\s\S]*?)\r?\n```/);
  if (!block) throw new Error('Missing public contract metadata');
  const contracts = block[1].split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
  const schemas = { ...toolDefinitions, corel_get_job: { schema: getJobSchema }, corel_guide: { schema: guideSchema } };
  for (const row of references.matchAll(/^\| `(corel_[a-z_]+)` \|/gm))
    check(Object.hasOwn(schemas, row[1]), `unknown tool row ${row[1]}`);
  const documented = new Map();
  for (const contract of contracts) {
    check(!documented.has(contract.tool), `duplicate tool ${contract.tool}`);
    check(Object.hasOwn(schemas, contract.tool), `unknown tool ${contract.tool}`);
    documented.set(contract.tool, contract);
  }
  for (const [name, { schema }] of Object.entries(schemas)) {
    if (toolDefinitions[name]) check(descriptions[name] === toolDefinitions[name].description, `description mismatch ${name}`);
    check(references.includes(`| \`${name}\` |`), `missing tool description ${name}`);
    const contract = documented.get(name);
    if (!contract) { failures.push(`missing tool metadata ${name}`); continue; }
    const input = z.toJSONSchema(schema, { io: 'input' });
    const defaults = {}, enums = {};
    for (const [key, field] of Object.entries(input.properties ?? {})) {
      if (Object.hasOwn(field, 'default')) defaults[key] = field.default;
      if (field.enum) enums[key] = field.enum;
    }
    check(isDeepStrictEqual(contract.required, input.required ?? []), `required fields mismatch ${name}`);
    check(isDeepStrictEqual(contract.defaults, defaults), `defaults mismatch ${name}`);
    check(isDeepStrictEqual(contract.enums, enums), `enum values mismatch ${name}`);
  }
  check(skill.includes('references/tools.md') && skill.includes('corel_execute_vba'), 'skill does not link public tools');
  for (const file of ['M_TextUnicode.bas', 'M_FillOutline.bas', 'M_DuplicateOffset.bas', 'M_LayerGroup.bas', 'M_BooleanShapes.bas', 'M_CurveNodes.bas', 'M_CommonHelpers.bas']) {
    check(existsSync(join(root, 'skills/coreldraw-automation/examples', file)), `missing recipe ${file}`);
    check(recipes.includes(file), `recipe ${file} is not documented`);
  }
} catch (error) {
  failures.push(error.message);
}
if (failures.length) { console.error(failures.map(failure => `contract: ${failure}`).join('\n')); process.exitCode = 1; }
else console.log('contracts ok');
