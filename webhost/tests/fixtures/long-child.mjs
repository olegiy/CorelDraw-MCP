import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { setTimeout as delay } from 'node:timers/promises';
import { JobManager } from '../../../dist/jobs.js';
import { createServer } from '../../../dist/server.js';

const executor = {
  async run(job, args, progress) {
    progress('long_fixture');
    await delay(3300);
    return { ok: true, result: { applied: true }, cleanup: { completed: true, errors: [] } };
  },
  close() {},
};
const jobs = new JobManager(process.env.CORELDRAW_MCP_RUNS, executor);
serveStdio(() => createServer(jobs));
