import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { existsSync } from 'node:fs';
import { connect, operation } from './helpers.mjs';

const worker = process.env.CORELDRAW_MCP_CSHARP_WORKER ?? fileURLToPath(new URL('../worker/publish/win-x64/CorelMcp.Worker.exe', import.meta.url));
test('a second Windows executor cannot enter the named application mutex', { skip: process.platform !== 'win32' || !existsSync(worker), timeout: 15000 }, async () => {
  const holder = spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-STA', '-File', fileURLToPath(new URL('./hold-mutex.ps1', import.meta.url))], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let errorOutput = '';
  holder.stderr.on('data', value => { errorOutput += value.toString(); });
  await Promise.race([
    once(holder.stdout, 'data').then(([value]) => assert.match(value.toString(), /READY/)),
    once(holder, 'exit').then(code => { throw new Error(`Mutex holder exited: ${code}: ${errorOutput}`); }),
  ]);
  const session = await connect({ runs: await mkdtemp(join(tmpdir(), 'corelmcp-mutex-')) });
  try {
    const report = await operation(session, 'corel_status');
    assert.equal(report.status, 'failed');
    assert.equal(report.error.code, 'BUSY');
  } finally { await session.close(); holder.stdin.end(); await once(holder, 'exit'); }
});
