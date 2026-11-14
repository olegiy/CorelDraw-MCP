import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { connect, call, operation } from './helpers.mjs';
import { toolDefinitions } from '../dist/tools.js';

for (const config of ['{"disabled_tools":["corel_edit_text","corel_export"]}',
                      '{"disabled_tools":[', '{"unknown":true}', ' '.repeat(65537)]) {
  test(`legacy configuration cannot hide tools (${config.length} bytes)`, async t => {
    const root = mkdtempSync(join(tmpdir(), 'corelmcp-config-'));
    t.after(() => rmSync(root, { recursive: true, force: true }));
    const path = join(root, 'config.json');
    writeFileSync(path, config);
    const session = await connect({ runs: join(root, 'runs'), fixture: true, configRoot: root });
    t.after(() => session.close());
    const names = (await session.client.listTools()).tools.map(tool => tool.name).sort();
    const defaultCatalog = Object.keys(toolDefinitions);
    assert.deepEqual(names, [...defaultCatalog, 'corel_get_job', 'corel_guide'].sort());
    const executed = await operation(session, 'corel_edit_text', {
      request_id: 'no-permission', document_id: 'fixture', page_index: 1, static_ids: [1001], action: 'set_text', text: 'fixture',
    });
    assert.equal(executed.status, 'succeeded');
    assert.equal(readFileSync(path, 'utf8'), config);
    const status = await operation(session, 'corel_status');
    assert.equal(Object.hasOwn(status, 'tool_policy'), false);
  });
}

test('Save/export accept no overwrite field and reject legacy arguments', async () => {
  for (const [name, args] of [
    ['corel_save_document', { request_id: 'save', document_id: 'fixture', path: 'C:/output/file.cdr' }],
    ['corel_export', { request_id: 'export', document_id: 'fixture', path: 'C:/output/file.png', format: 'png' }],
  ]) {
    assert.equal(Object.hasOwn(toolDefinitions[name].schema.parse(args), 'overwrite'), false);
    assert.throws(() => toolDefinitions[name].schema.parse({ ...args, overwrite: true }));
    assert.throws(() => toolDefinitions[name].schema.parse({ ...args, overwrite: false }));
  }
  assert.equal(toolDefinitions.corel_export_diagnostics.schema.parse({
    request_id: 'diagnostic', job_id: '00000000-0000-0000-0000-000000000001', path: 'C:/output/report.json',
  }).overwrite, false);
});

test('historical tool_policy survives polling and compaction as evidence', async t => {
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-history-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const session = await connect({ runs: root, fixture: true });
  t.after(() => session.close());
  const status = await operation(session, 'corel_status');
  const legacy = { source: 'user', config_path: 'C:/legacy/config.json', disabled_count: 2 };
  writeFileSync(status.report_path, JSON.stringify({ ...status, tool_policy: legacy }));
  assert.deepEqual((await call(session, 'corel_get_job', { job_id: status.job_id })).tool_policy, legacy);
  const { ArtifactStore } = await import('../dist/artifacts.js');
  const artifacts = new ArtifactStore(root, id => JSON.parse(readFileSync(join(root, id, 'report.json'), 'utf8')));
  const inventory = artifacts.list(0, 100).items.find(item => item.job_id === status.job_id);
  assert.equal(artifacts.prune([{ job_id: status.job_id, prune_token: inventory.prune_token }]).items[0].pruned, true);
  assert.deepEqual(JSON.parse(readFileSync(status.report_path, 'utf8')).tool_policy, legacy);
});
