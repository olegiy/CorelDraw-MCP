import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

test('PowerShell cleanup isolates failures and preserves the export primary error', { skip: process.platform !== 'win32' }, () => {
  const run = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', fileURLToPath(new URL('./powershell-cleanup.ps1', import.meta.url))], { encoding: 'utf8' });
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.match(run.stdout, /cleanup fixtures passed/);
});
