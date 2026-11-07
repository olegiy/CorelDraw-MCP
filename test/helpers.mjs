import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

export async function connect({ runs, fixture = false, configRoot, fixtureDelayMs } = {}) {
  const transport = new StdioClientTransport({ command: process.execPath,
    args: [fileURLToPath(new URL(fixture ? './fixture-server.mjs' : '../dist/index.js', import.meta.url))],
    env: Object.fromEntries(Object.entries({ ...process.env, ...(runs ? { CORELDRAW_MCP_RUNS: runs } : {}), ...(configRoot ? { CORELDRAW_MCP_CONFIG_ROOT: configRoot } : {}), ...(fixtureDelayMs ? { CORELDRAW_MCP_TEST_DELAY_MS: String(fixtureDelayMs) } : {}) }).filter(([, value]) => value !== undefined)),
    stderr: 'pipe',
  });
  let stderr = '';
  transport.stderr?.on('data', data => { stderr += data.toString(); });
  const client = new Client({ name: 'corel-mcp-tests', version: '0.1.0' });
  await client.connect(transport);
  const pending = new Set();
  const session = { client, transport, pending, stderr: () => stderr, close: async () => {
    // The SDK has a finite-time child kill fallback. Enter close only after
    // every accepted native job has actually finished, not just timed out.
    for (const job_id of pending) {
      while (true) {
        const report = await call(session, 'corel_get_job', { job_id, wait_seconds: 1 });
        if (report.status === 'unknown' && !fixture) throw new Error(`Native outcome unknown; executor retained: ${job_id}`);
        if (report.finished_at) break;
        await delay(20);
      }
      pending.delete(job_id);
    }
    await client.close();
  } };
  return session;
}
export function data(result) {
  if (result.structuredContent) return result.structuredContent;
  const text = result.content.find(block => block.type === 'text')?.text ?? '';
  try { return JSON.parse(text); } catch { return { error: { code: 'MCP_INPUT_ERROR', message: text } }; }
}
export async function call(session, name, args = {}) {
  return data(await session.client.callTool({ name, arguments: args }));
}
export async function operation(session, name, args = {}, timeoutMs = 60_000) {
  const job = await call(session, name, args);
  if (job.error) throw new Error(JSON.stringify(job));
  session.pending.add(job.job_id);
  const until = Date.now() + timeoutMs;
  while (Date.now() < until) {
    const report = await call(session, 'corel_get_job', { job_id: job.job_id, wait_seconds: 1 });
    if (report.status !== 'running') {
      if (report.finished_at && report.status !== 'unknown') session.pending.delete(job.job_id);
      return report;
    }
    await delay(20);
  }
  throw new Error(`Test polling timeout: ${job.job_id}`);
}
