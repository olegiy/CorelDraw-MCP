import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { createExecutorFromEnvironment } from './backend.js';
import { JobManager } from './jobs.js';
import { createServer } from './server.js';
import { VERSION } from './version.js';

// Validate host configuration before accepting MCP requests or touching COM.
const executor = createExecutorFromEnvironment();

const root =
  process.env.CORELDRAW_MCP_RUNS ??
  join(process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local'), 'CorelDrawMcp', 'runs');
const jobs = new JobManager(
  root,
  executor,
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
