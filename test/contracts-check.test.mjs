import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, cpSync, readFileSync, writeFileSync, rmSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const script = join(root, 'scripts/check-contracts.mjs');
const run = directory => spawnSync(process.execPath, [script, '--root', directory], { cwd: root, encoding: 'utf8' });

test('contract checker accepts repository and rejects real corrupted documentation and files', async (t) => {
  const valid = run(root);
  assert.equal(valid.status, 0, valid.stderr);
  const cases = [
    ['missing tool', 'references/tools.md', text => text.replace(/^\| `corel_export`.*\r?\n/m, ''), /corel_export/],
    ['required schema field', 'references/tools.md', text => text.replace('"tool":"corel_create_document","required":["request_id"]', '"tool":"corel_create_document","required":[]'), /required.*corel_create_document/],
    ['stale preview target', 'references/tools.md', text => text.replace('"target":["page","selection"]', '"target":["page"]'), /enum.*corel_preview/],
    ['stale export alias', 'references/tools.md', text => text.replace('"jpg","jpeg"', '"jpg","jpegg"'), /enum.*corel_export/],
    ['undocumented default', 'references/tools.md', text => text.replace('"max_size":1200', '"max_size":2400'), /default.*corel_preview/],
    ['missing recipe', 'examples/text-unicode.json', null, /text-unicode/],
    ['stale description', 'references/tools.md', text => text.replace(/("corel_preview"\s*:\s*")[^"]*/, '$1Обещание PDF вместо PNG'), /description.*corel_preview/],
    ['stale tool row', 'references/tools.md', text => text + '\n| `corel_removed` | obsolete |\n', /unknown tool.*corel_removed/],
  ];
  for (const [label, file, corrupt, expected] of cases) await t.test(label, () => {
    const directory = mkdtempSync(join(tmpdir(), 'corelmcp-contract-'));
    t.after(() => rmSync(directory, { recursive: true, force: true }));
    cpSync(join(root, 'skills'), join(directory, 'skills'), { recursive: true });
    cpSync(join(root, 'dist'), join(directory, 'dist'), { recursive: true });
    writeFileSync(join(directory, 'package.json'), '{"type":"module"}');
    symlinkSync(join(root, 'node_modules'), join(directory, 'node_modules'), process.platform === 'win32' ? 'junction' : 'dir');
    const path = join(directory, 'skills/coreldraw-automation', file);
    if (corrupt) {
      const original = readFileSync(path, 'utf8'), changed = corrupt(original);
      assert(changed !== original, `fixture mutation not applied: ${label}`);
      writeFileSync(path, changed);
    } else rmSync(path);
    const result = run(directory);
    assert.equal(result.status, 1, result.stdout);
    assert.match(result.stderr, expected);
  });
  await t.test('schema in selected root is authoritative', () => {
    const directory = mkdtempSync(join(tmpdir(), 'corelmcp-contract-'));
    t.after(() => rmSync(directory, { recursive: true, force: true }));
    cpSync(join(root, 'skills'), join(directory, 'skills'), { recursive: true });
    cpSync(join(root, 'dist'), join(directory, 'dist'), { recursive: true });
    writeFileSync(join(directory, 'package.json'), '{"type":"module"}');
    symlinkSync(join(root, 'node_modules'), join(directory, 'node_modules'), process.platform === 'win32' ? 'junction' : 'dir');
    const path = join(directory, 'dist/tools.js');
    const source = readFileSync(path, 'utf8');
    const changed = source.replace("target: z.enum(['page', 'selection'])", "target: z.enum(['page'])");
    assert(source !== changed, 'schema fixture mutation not applied');
    writeFileSync(path, changed);
    const result = run(directory);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /enum.*corel_preview/);
  });
});
