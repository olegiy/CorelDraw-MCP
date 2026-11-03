import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { JobManager } from '../dist/jobs.js';
import { createServer } from '../dist/server.js';

const fixture = {
  async run(job, args, progress) {
    progress('fixture_running');
    if (args.code === 'lose executor') throw new Error('Synthetic executor loss');
    if (args.code === 'delay') await delay(1400);
    if (args.code === 'fail') return { ok: false, error: { code: 'VBA_RUNTIME_ERROR', stage: 'executing_vba', message: 'Synthetic failure', number: 123 } };
    if (job.tool === 'corel_preview') {
      const path = join(job.run_directory, 'preview.png');
      writeFileSync(path, Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/vYEAAAAASUVORK5CYII=', 'base64'));
      return { ok: true, result: { path }, artifacts: [path] };
    }
    return { ok: true, result: { received: args, fixture: true }, cleanup: { completed: true } };
  },
  close() {},
};
const jobs = new JobManager(process.env.CORELDRAW_MCP_RUNS, fixture);
serveStdio(() => createServer(jobs));
