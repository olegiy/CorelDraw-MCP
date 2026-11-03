import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { PowerShellExecutor } from './executor.js';
import { JobManager } from './jobs.js';
import { createServer } from './server.js';

const root = process.env.CORELDRAW_MCP_RUNS ?? join(process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local'), 'CorelDrawMcp', 'runs');
const jobs = new JobManager(root, new PowerShellExecutor(process.env.CORELDRAW_MCP_PROGID ?? 'CorelDRAW.Application.27'));
const handle = serveStdio(() => createServer(jobs));
process.on('SIGINT', () => { jobs.close(); void handle.close(); });
process.on('SIGTERM', () => { jobs.close(); void handle.close(); });
console.error('coreldraw MCP 0.1.0 ready on stdio');
