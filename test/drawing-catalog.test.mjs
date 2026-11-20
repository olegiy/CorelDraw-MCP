import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Client, InMemoryTransport } from '@modelcontextprotocol/client';
import { createServer } from '../dist/server.js';
import { JobManager } from '../dist/jobs.js';

for (const backend of [undefined, 'csharp']) {
  test(`${backend} publishes only its executable catalog with full variant schemas`, async t => {
    const root = await mkdtemp(join(tmpdir(), 'corelmcp-catalog-'));
    let calls = 0;
    const executor = { backend, run: async (_job, args) => { calls++; return { ok: true, result: args }; }, close() {} };
    const jobs = new JobManager(root, executor);
    const server = createServer(jobs);
    const client = new Client({ name: 'catalog-test', version: '1' });
    const [serverTransport, clientTransport] = InMemoryTransport.createLinkedPair();
    await server.connect(serverTransport); await client.connect(clientTransport);
    t.after(async () => { await client.close(); await server.close(); });
    const list = (await client.listTools()).tools;
    assert.equal(list.length, 43);
    assert.equal(list.some(tool => tool.name === 'corel_list_macros'), true);
    assert.equal(list.some(tool => tool.name === 'corel_run_macro'), true);
    assert.equal(list.some(tool => tool.name === 'corel_execute_vba'), false);
    assert.equal(list.some(tool => tool.name === 'corel_build_vba'), false);
    assert.equal(list.some(tool => tool.name === 'corel_create_shapes'), true);
    assert.equal(list.some(tool => tool.name === 'corel_create_batch'), true);
    assert.equal(list.some(tool => tool.name === 'corel_update_batch'), true);
    assert.equal(list.some(tool => tool.name === 'corel_set_transparency'), true);
    assert.equal(list.some(tool => tool.name === 'corel_apply_drop_shadow'), true);
    if (true) {
      const transform = list.find(tool => tool.name === 'corel_transform_shapes');
      assert.equal(transform.inputSchema.type, 'object');
      assert.ok(transform.inputSchema.oneOf?.length === 4 || transform.inputSchema.anyOf?.length === 4);
      const result = await client.callTool({ name: 'corel_create_shapes', arguments: { request_id: 'invalid', document_id: 'doc', page_index: 1, layer_id: 'layer', items: [{ kind: 'rectangle', x_mm: 0, y_mm: 0, width_mm: -1, height_mm: 1 }] } });
      assert.equal(result.isError, true); assert.equal(calls, 0);
    }
    const send = (name, arguments_) => client.callTool({ name, arguments: arguments_ });
    const macro = {
      request_id: 'copy-exactly-once', document_id: 'doc', page_index: 1,
      macro_id: 'duplicate_demo', static_ids: [4], parameters: { count: 3 },
    };
    const catalog = (await send('corel_list_macros', {})).structuredContent;
    assert.equal(catalog.macros[0].macro_id, 'duplicate_demo');
    assert.equal(calls, 0);
    for (const bad of [{ ...macro, macro_id: 'anything_else' }, { ...macro, code: 'Sub A()' },
      { ...macro, parameters: { count: 2.5 } }]) {
      assert.equal((await send('corel_run_macro', bad)).isError, true);
      assert.equal(calls, 0);
    }
    const accepted = (await send('corel_run_macro', macro)).structuredContent;
    const report = (await send('corel_get_job', { job_id: accepted.job_id, wait_seconds: 1 })).structuredContent;
    assert.equal(report.status, 'succeeded');
    assert.deepEqual(report.result.parameters, { count: 3 });
    assert.equal((await send('corel_run_macro', macro)).structuredContent.job_id, accepted.job_id);
    assert.equal(calls, 1);
  });
}
