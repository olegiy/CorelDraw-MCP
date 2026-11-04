import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { connect, operation, data } from './helpers.mjs';

for (const kind of ['directory', 'empty', 'oversized', 'outside']) {
  test(`preview refuses ${kind} artifact without attaching image`, async t => {
    const runs = await mkdtemp(join(tmpdir(), 'corelmcp-preview-'));
    const session = await connect({ fixture: true, runs });
    t.after(async () => { await session.close(); await rm(runs, { recursive: true, force: true }); });
    const job = await operation(session, 'corel_preview', { document_id: 'fixture' });
    const report = JSON.parse(await readFile(job.report_path, 'utf8'));
    const path = kind === 'outside' ? join(runs, 'outside.png') : join(job.run_directory, 'invalid.png');
    if (kind === 'directory') await mkdir(path);
    else await writeFile(path, kind === 'oversized' ? Buffer.alloc(10 * 1024 * 1024 + 1) : Buffer.alloc(0));
    report.result.path = path;
    report.artifacts = [path];
    await writeFile(job.report_path, JSON.stringify(report));
    const result = await session.client.callTool({ name: 'corel_get_job', arguments: { job_id: job.job_id, include_preview: true } });
    assert.equal(result.isError, true);
    assert.equal(result.content.some(block => block.type === 'image'), false);
    assert.equal(data(result).error.code, kind === 'outside' ? 'PREVIEW_INVALID_PATH' : 'PREVIEW_INVALID_FILE');
  });
}
