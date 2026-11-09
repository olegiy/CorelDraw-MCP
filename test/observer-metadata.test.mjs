import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

test('worker verifies typelib members before invoking HWND getter and preserves signed Int32 bits', { skip: process.platform !== 'win32' }, () => {
  const result = spawnSync('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-STA', '-File',
    fileURLToPath(new URL('./observer-metadata.fixture.ps1', import.meta.url))], { windowsHide: true, shell: false, encoding: 'utf8', timeout: 15000 });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.match(result.stdout, /observer metadata fixtures passed/);
});
