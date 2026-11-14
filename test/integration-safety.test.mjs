import assert from 'node:assert/strict';
import { mkdtemp, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { test } from 'node:test';
import { call, connect, operation } from './helpers.mjs';

test('integration helper drains known delayed job after its polling deadline', async () => {
  const runs = await mkdtemp(join(tmpdir(), 'corel-live-helper-'));
  const session = await connect({ fixture: true, runs, fixtureDelayMs: 4500 });
  let job;
  try {
    await assert.rejects(operation(session, 'corel_edit_text', {
      request_id: 'helper-delayed-job', document_id: 'fixture-document', page_index: 1, static_ids: [1001], action: 'set_text', text: 'delay',
    }, 1), /Test polling timeout/);
    job = (await call(session, 'corel_edit_text', {
      request_id: 'helper-delayed-job', document_id: 'fixture-document', page_index: 1, static_ids: [1001], action: 'set_text', text: 'delay',
    })).job_id;
  } finally {
    await session.close();
  }
  const report = JSON.parse(await readFile(join(runs, job, 'report.json'), 'utf8'));
  assert.equal(report.status, 'succeeded');
  assert.equal(report.cleanup.completed, true);
});
