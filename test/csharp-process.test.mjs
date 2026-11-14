import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { CSharpExecutor } from '../dist/executor.js';

test('real C# process and TS executor agree on ready, strict result DTO and graceful EOF', {
  skip: process.env.CORELDRAW_WORKER_PROTOCOL_TEST !== '1', timeout: 20000,
}, async () => {
  let child;
  let exited;
  const worker = fileURLToPath(new URL('../worker/CorelMcp.Worker/bin/Release/net10.0-windows/CorelMcp.Worker.dll', import.meta.url));
  const executor = new CSharpExecutor({ launchWorker: () => {
    child = spawn(process.env.DOTNET_HOST_PATH ?? 'dotnet', [worker], { shell: false, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    exited = new Promise(resolve => child.once('close', resolve));
    return child;
  } });
  try {
    const result = await executor.run({ job_id: randomUUID(), tool: 'corel_execute_vba',
      run_directory: mkdtempSync(join(tmpdir(), 'corelmcp-protocol-')) }, {}, () => {});
    assert.equal(result.ok, false);
    assert.equal(result.error.code, 'UNKNOWN_TOOL');
    assert.equal(result.error.applied, false);
    assert.match(result.versions.runtime, /^\.NET 10\./);
    assert.equal(result.versions.protocol, '1');
    assert.equal(result.versions.architecture, 'x64');
  } finally { executor.close(); }
  assert.equal(await exited, 0);
});
