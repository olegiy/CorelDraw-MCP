import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

test('contract checker accepts the repository contracts', () => {
  const result = spawnSync(process.execPath, [join(root, 'scripts/check-contracts.mjs')], { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.match(result.stdout, /contracts ok/i);
});

test('contract checker reports invalid contract fixtures', () => {
  const result = spawnSync(process.execPath, [join(root, 'scripts/check-contracts.mjs'), '--fixture-invalid'], { cwd: root, encoding: 'utf8' });
  assert.notEqual(result.status, 0);
  assert.match(`${result.stdout}\n${result.stderr}`, /missing recipe|preview target|contract/i);
});

test('contract checker is tied to the declared preview target enum', () => {
  const result = spawnSync(process.execPath, [join(root, 'scripts/check-contracts.mjs'), '--fixture-target-invalid'], { cwd: root, encoding: 'utf8' });
  assert.notEqual(result.status, 0);
  assert.match(`${result.stdout}\n${result.stderr}`, /target/i);
});
