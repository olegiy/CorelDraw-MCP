import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

test('Get-CorelApiMetadata reflects a real COM type library with paging', { skip: process.platform !== 'win32' }, () => {
  const script = fileURLToPath(new URL('./api-metadata.fixture.ps1', import.meta.url));
  const run = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', script], { encoding: 'utf8' });
  assert.equal(run.status, 0, run.stdout + run.stderr);
  const result = JSON.parse(run.stdout.trim());
  assert.equal(result.kind, 'types');
  assert.ok(result.total > 0);
  assert.ok(Array.isArray(result.items));
  assert.ok(result.items.length <= 2);
  assert.equal(result.offset, 0);
  assert.equal(result.limit, 2);
  assert.ok(result.library.guid);
  assert.ok(result.library.major >= 1);
  assert.equal(result.next_offset, result.total > 2 ? 2 : null);
});

test('Get-CorelApiMetadata rejects expression-like names', { skip: process.platform !== 'win32' }, () => {
  const script = fileURLToPath(new URL('./api-metadata.fixture.ps1', import.meta.url));
  const run = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', script, '-InvalidName'], { encoding: 'utf8' });
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.match(run.stdout, /INVALID_NAME/);
});

test('Get-CorelApiMetadata returns COM method parameters and enum constants', { skip: process.platform !== 'win32' }, () => {
  const script = fileURLToPath(new URL('./api-metadata.fixture.ps1', import.meta.url));
  const run = (...args) => {
    const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', script, ...args], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    return JSON.parse(result.stdout.trim());
  };
  const members = run('-Kind', 'members', '-Name', 'IDictionary', '-Filter', 'Add');
  const add = members.items.find(item => item.name === 'Add');
  assert.ok(add);
  assert.equal(add.parameters.length, 2);
  assert.equal(typeof add.parameters[0].optional, 'boolean');
  const values = run('-Kind', 'enum', '-Name', 'CompareMethod');
  assert.equal(values.total, 3);
  assert.equal(values.items[0].value, 0);
  assert.equal(values.items[0].readonly, true);
  const coclass = run('-Kind', 'members', '-Name', 'Dictionary', '-Filter', 'Add');
  assert.ok(coclass.items.some(item => item.name === 'Add'));
  const alias = run('-Kind', 'enum', '-Name', 'FileAttribute');
  assert.ok(alias.total > 0);
  assert.equal(alias.items[0].readonly, true);
  const optional = run('-Kind', 'members', '-Name', 'IFileSystem', '-Filter', 'CreateTextFile');
  assert.equal(optional.items[0].parameters[1].optional, true);
  assert.equal(optional.items[0].parameters[1].default, true);
  assert.equal(optional.items[0].parameters[2].default, false);
});
