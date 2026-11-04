import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { PowerShellExecutor } from './executor.js';
import { JobManager } from './jobs.js';
import { createServer } from './server.js';
import { VERSION } from './version.js';
import { loadToolPolicy } from './config.js';

const root =
  process.env.CORELDRAW_MCP_RUNS ??
  join(process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local'), 'CorelDrawMcp', 'runs');
const jobs = new JobManager(
  root,
  new PowerShellExecutor(process.env.CORELDRAW_MCP_PROGID ?? 'CorelDRAW.Application.27'),
);
const policyRoot = process.env.CORELDRAW_MCP_CONFIG_ROOT ?? join(process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local'), 'CorelDrawMcp');
const handle = serveStdio(() => createServer(jobs, loadToolPolicy(policyRoot)));
process.on('SIGINT', () => {
  jobs.close();
  void handle.close();
});
process.on('SIGTERM', () => {
  jobs.close();
  void handle.close();
});
console.error(`coreldraw MCP ${VERSION} ready on stdio`);
