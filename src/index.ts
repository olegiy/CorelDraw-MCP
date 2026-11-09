import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { PowerShellExecutor } from './executor.js';
import { JobManager } from './jobs.js';
import { createServer } from './server.js';
import { VERSION } from './version.js';
import { observerMode } from './vba-observer.js';

// Validate host configuration before accepting MCP requests or touching COM.
const vbaObserverMode = observerMode(process.env.CORELDRAW_MCP_VBA_OBSERVER);

const root =
  process.env.CORELDRAW_MCP_RUNS ??
  join(process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local'), 'CorelDrawMcp', 'runs');
const jobs = new JobManager(
  root,
  new PowerShellExecutor(process.env.CORELDRAW_MCP_PROGID ?? 'CorelDRAW.Application.27', undefined, vbaObserverMode),
);
const handle = serveStdio(() => createServer(jobs));
process.stdin.once('end', () => jobs.close());
process.on('SIGINT', () => {
  jobs.close();
  void handle.close();
});
process.on('SIGTERM', () => {
  jobs.close();
  void handle.close();
});
console.error(`coreldraw MCP ${VERSION} ready on stdio`);
