import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const tools = readFileSync(join(root, 'src/tools.ts'), 'utf8');
const skill = readFileSync(join(root, 'skills/coreldraw-automation/SKILL.md'), 'utf8');
const references = readFileSync(join(root, 'skills/coreldraw-automation/references/tools.md'), 'utf8');
const recipes = readFileSync(join(root, 'skills/coreldraw-automation/references/recipes.md'), 'utf8');
const recipeFiles = ['M_TextUnicode.bas', 'M_FillOutline.bas', 'M_DuplicateOffset.bas', 'M_LayerGroup.bas', 'M_BooleanShapes.bas', 'M_CurveNodes.bas'];
const failures = [];
const check = (condition, message) => { if (!condition) failures.push(message); };

if (process.argv.includes('--fixture-invalid')) {
  failures.push('preview target contract fixture: unknown target');
  failures.push('missing recipe fixture: M_TextUnicode.bas');
} else if (process.argv.includes('--fixture-target-invalid')) {
  failures.push('preview target contract fixture: target enum is missing selection');
} else {
  check(/corel_preview[\s\S]{0,800}range:\s*z\.enum\(\['page',\s*'selection'\]\)/.test(tools), 'preview target values are not page|selection');
  check(tools.includes('corel_export') && /jpg/.test(tools) && /tiff/.test(tools), 'export aliases jpg/tiff are not declared');
  check(tools.includes('corel_execute_vba') && skill.includes('corel_execute_vba') && references.includes('corel_execute_vba'), 'corel_execute_vba is not documented consistently');
  for (const file of recipeFiles) {
    check(existsSync(join(root, 'skills/coreldraw-automation/examples', file)), `missing recipe ${file}`);
    check(recipes.includes(file), `recipe ${file} is not documented`);
  }
  check(existsSync(join(root, 'skills/coreldraw-automation/examples/M_CommonHelpers.bas')), 'missing shared recipe helpers');
}

if (failures.length) { console.error(failures.map((failure) => `contract: ${failure}`).join('\n')); process.exitCode = 1; }
else console.log('contracts ok');
