import assert from 'node:assert/strict';
import { readFile, readdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const directory = process.argv[2];
const load = async path => JSON.parse(await readFile(path, 'utf8'));
const before = await load(join(directory, 'before.json'));
const after = await load(join(directory, 'after.json'));
const proof = await load(join(directory, 'verification.json'));
delete before.projects;
delete after.projects;
assert.deepEqual(after, before, 'Corel user documents/application state changed');
const roots = (await readdir(directory)).filter(name => /^CorelDrawMcp-(integration|recipes|extended)-/.test(name));
assert.equal(roots.length, 3);
let count = 0;
for (const root of roots) {
  const report = await load(join(directory, root, 'verification.json'));
  assert.equal(report.ok, true, root);
  count += report.checks.length;
}
assert.equal(proof.suites.length, 3);
proof.preservation = true;
proof.native_checks_passed = true;
proof.ok = proof.cleanup_completed === true;
proof.native_check_count = count;
await writeFile(join(directory, 'verification.json'), JSON.stringify(proof, null, 2));
console.log(`M3_PRESERVATION_PASS NATIVE_CHECKS=${count}`);
