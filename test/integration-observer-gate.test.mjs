import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

test('live observer smoke is opt-in and exits before connecting to CorelDRAW', () => {
  const result = spawnSync(process.execPath, [fileURLToPath(new URL('./integration-vba-observer.mjs', import.meta.url))],
    { env: { ...process.env, CORELDRAW_MCP_LIVE_OBSERVER: '', CORELDRAW_MCP_PROGID: 'INVALID.NEVER.LAUNCH' }, encoding: 'utf8', windowsHide: true, timeout: 10000 });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Пропуск/);
});
