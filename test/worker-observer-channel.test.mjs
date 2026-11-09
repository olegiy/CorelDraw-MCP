import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, copyFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

test('PS 5.1 stdin dispatcher ignores late observer replies and keeps the next job and JSON validation',
  { skip: process.platform !== 'win32', timeout: 15000 }, async () => {
  const directory = mkdtempSync(join(tmpdir(), 'corelmcp-worker-channel-'));
  for (const name of ['worker.ps1', 'JsonLineChannel.cs']) copyFileSync(new URL('../powershell/' + name, import.meta.url), join(directory, name));
  writeFileSync(join(directory, 'CorelAutomation.psm1'), '\uFEFF' + `
function Invoke-CorelOperation {
  param($Tool, $Parameters, $RunDirectory, $ProgId, $Progress, $ValidateJson, $Observer)
  $reply = & $Observer 'prepare' 'compiling' 'CorelMcp_fixture' '42'
  & $ValidateJson '[]'
  return @{ ok = $true; result = @{ available = [bool]$reply.available; marker = $Parameters.marker } }
}
Export-ModuleMember Invoke-CorelOperation
`);
  const child = spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-STA', '-File', join(directory, 'worker.ps1')],
    { windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'] });
  let stderr = '', delayed;
  child.stderr.on('data', data => { stderr += data.toString(); });
  const lines = createInterface({ input: child.stdout });
  const results = [];
  const completed = new Promise((resolve, reject) => {
    child.once('error', reject);
    lines.on('line', line => {
      const message = JSON.parse(line.replace(/^\uFEFF/, ''));
      if (message.type === 'observer_prepare') {
        const reply = { schema_version: 1, type: 'observer_ready', job_id: message.job_id, handshake_id: message.handshake_id, available: true };
        if (message.job_id === 'first') delayed = reply;
        else child.stdin.write(JSON.stringify(reply) + '\n');
      } else if (message.type === 'validate_json') {
        if (delayed) { child.stdin.write(JSON.stringify(delayed) + '\n'); delayed = undefined; }
        child.stdin.write(JSON.stringify({ type: 'json_validation', job_id: message.job_id, ok: true }) + '\n');
      } else if (message.type === 'result') {
        results.push(message);
        if (results.length === 1) submit('second');
        else { child.stdin.end(); resolve(); }
      }
    });
    child.once('exit', code => { if (results.length < 2) reject(new Error(`Worker ${code}: ${stderr}`)); });
  });
  const submit = id => child.stdin.write(JSON.stringify({ job_id: id, tool: 'fixture', args: { marker: id }, run_directory: directory, observer_mode: 'observe' }) + '\n');
  try {
    submit('first'); await completed;
    assert.equal(results[0].data.result.available, false);
    assert.equal(results[1].data.result.available, true);
    assert.deepEqual(results.map(value => value.data.result.marker), ['first', 'second']);
  } finally {
    child.stdin.end();
    if (child.exitCode === null) await new Promise(resolve => {
      const timer = setTimeout(() => child.kill(), 2000);
      child.once('exit', () => { clearTimeout(timer); lines.close(); resolve(); });
    });
  }
});
