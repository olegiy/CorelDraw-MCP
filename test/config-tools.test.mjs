import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, mkdirSync, readdirSync, symlinkSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { fileURLToPath } from 'node:url';

test('tool policy enables all tools when no config exists', async () => {
  const { loadToolPolicy } = await import('../dist/config.js');
  const policy = loadToolPolicy(mkdtempSync(join(tmpdir(), 'corelmcp-config-')));
  assert.equal(policy.source, 'default');
  assert.equal(policy.isEnabled('corel_execute_vba'), true);
  assert.equal(policy.isEnabled('corel_status'), true);
});

test('tool policy disables configured tools and keeps read-only status available', async () => {
  const { loadToolPolicy } = await import('../dist/config.js');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-config-'));
  writeFileSync(join(root, 'config.json'), JSON.stringify({ disabled_tools: ['corel_execute_vba', 'corel_export'] }));
  const policy = loadToolPolicy(root);
  assert.equal(policy.source, 'user');
  assert.equal(policy.isEnabled('corel_execute_vba'), false);
  assert.equal(policy.isEnabled('corel_export'), false);
  assert.equal(policy.isEnabled('corel_status'), true);
});

test('malformed tool policy fails closed for mutation tools', async () => {
  const { loadToolPolicy } = await import('../dist/config.js');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-config-'));
  writeFileSync(join(root, 'config.json'), '{"disabled_tools": [');
  const policy = loadToolPolicy(root);
  assert.equal(policy.source, 'invalid');
  assert.equal(policy.isEnabled('corel_execute_vba'), false);
  assert.equal(policy.isEnabled('corel_export'), false);
  assert.equal(policy.isEnabled('corel_status'), true);
});

test('server omits disabled tools before MCP registration', async (t) => {
  const configRoot = mkdtempSync(join(tmpdir(), 'corelmcp-config-'));
  writeFileSync(join(configRoot, 'config.json'), JSON.stringify({ disabled_tools: ['corel_execute_vba'] }));
  const runs = mkdtempSync(join(tmpdir(), 'corelmcp-runs-'));
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [fileURLToPath(new URL('../dist/index.js', import.meta.url))],
    env: { ...process.env, CORELDRAW_MCP_CONFIG_ROOT: configRoot, CORELDRAW_MCP_RUNS: runs },
    stderr: 'pipe',
  });
  const client = new Client({ name: 'config-test', version: '1.0.0' });
  t.after(() => client.close());
  await client.connect(transport);
  const list = await client.listTools();
  assert.equal(list.tools.some((tool) => tool.name === 'corel_execute_vba'), false);
  assert.equal(list.tools.some((tool) => tool.name === 'corel_status'), true);
  await assert.rejects(client.callTool({ name: 'corel_execute_vba', arguments: { request_id: 'blocked', document_id: 'anything', code: 'invalid' } }));
  assert.deepEqual(readdirSync(runs), []);
});

test('unknown fields, unknown tools, duplicates and invalid lists fail closed', async (t) => {
  const { loadToolPolicy } = await import('../dist/config.js');
  for (const value of [
    { disabled_tools: [], enabled_tools: ['corel_execute_vba'] },
    { disabled_tools: ['corel_typo'] },
    { disabled_tools: ['corel_export', 'corel_export'] },
    { disabled_tools: Array(101).fill('corel_export') },
    { disabled_tools: null }, { disabled_tools: 'corel_export' }, [], null,
  ]) await t.test(JSON.stringify(value).slice(0, 90), () => {
    const root = mkdtempSync(join(tmpdir(), 'corelmcp-config-'));
    t.after(() => rmSync(root, { recursive: true, force: true }));
    writeFileSync(join(root, 'config.json'), JSON.stringify(value));
    const policy = loadToolPolicy(root);
    assert.equal(policy.source, 'invalid');
    assert.equal(policy.isEnabled('corel_execute_vba'), false);
    assert.equal(policy.isEnabled('corel_status'), true);
    assert.equal(policy.isEnabled('corel_preview'), true);
    assert.equal(policy.isEnabled('corel_typo'), false);
  });
});

test('oversized, non-file and linked configuration cannot enable mutations', async (t) => {
  const { loadToolPolicy } = await import('../dist/config.js');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-config-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  writeFileSync(join(root, 'config.json'), `${' '.repeat(65536)}{"disabled_tools":[]}`);
  assert.equal(loadToolPolicy(root).source, 'invalid');
  rmSync(join(root, 'config.json'));
  mkdirSync(join(root, 'config.json'));
  assert.equal(loadToolPolicy(root).source, 'invalid');
  writeFileSync(join(root, 'not-directory'), 'file');
  assert.equal(loadToolPolicy(join(root, 'not-directory')).source, 'invalid');
  mkdirSync(join(root, 'real'));
  writeFileSync(join(root, 'real', 'config.json'), '{"disabled_tools":[]}');
  symlinkSync(join(root, 'real'), join(root, 'linked'), process.platform === 'win32' ? 'junction' : 'dir');
  assert.equal(loadToolPolicy(join(root, 'linked')).source, 'invalid');
});

test('explicit inspection disable is honored while local recovery diagnostics stay available', async () => {
  const { loadToolPolicy } = await import('../dist/config.js');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-config-'));
  writeFileSync(join(root, 'config.json'), JSON.stringify({ disabled_tools: ['corel_inspect_document', 'corel_preview', 'corel_status', 'corel_get_job', 'corel_guide', 'corel_list_artifacts'] }));
  const policy = loadToolPolicy(root);
  assert.equal(policy.source, 'user');
  assert.equal(policy.isEnabled('corel_inspect_document'), false);
  assert.equal(policy.isEnabled('corel_preview'), false);
  for (const name of ['corel_status', 'corel_get_job', 'corel_guide', 'corel_list_artifacts']) assert.equal(policy.isEnabled(name), true);
  assert.equal(policy.summary().disabled_count, 2);
  rmSync(root, { recursive: true, force: true });
});

test('atomic policy writer preserves original on invalid input or unsafe destination', async (t) => {
  const { loadToolPolicy, writeToolPolicy } = await import('../dist/config.js');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-config-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const path = writeToolPolicy(root, { disabled_tools: ['corel_export'] });
  const original = readFileSync(path, 'utf8');
  assert.equal(loadToolPolicy(root).isEnabled('corel_export'), false);
  assert.throws(() => writeToolPolicy(root, { disabled_tools: ['corel_typo'] }));
  assert.equal(readFileSync(path, 'utf8'), original);
  writeToolPolicy(root, { disabled_tools: [] });
  assert.equal(loadToolPolicy(root).isEnabled('corel_export'), true);
  assert.deepEqual(readdirSync(root), ['config.json']);
  mkdirSync(join(root, 'real'));
  symlinkSync(join(root, 'real'), join(root, 'linked'), process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => writeToolPolicy(join(root, 'linked'), { disabled_tools: [] }));
  assert.deepEqual(readdirSync(join(root, 'real')), []);
});

test('status policy counts survive polling and persisted report without COM', async (t) => {
  const { connect, call, operation } = await import('./helpers.mjs');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-config-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  writeFileSync(join(root, 'config.json'), '{"disabled_tools":["corel_export"]}');
  const session = await connect({ runs: join(root, 'runs'), fixture: true, configRoot: root });
  t.after(() => session.close());
  const list = await session.client.listTools();
  const job = await operation(session, 'corel_status');
  assert.equal(job.tool_policy.source, 'user');
  assert.equal(job.tool_policy.effective_count, 17);
  assert.equal(job.tool_policy.registered_count, list.tools.length);
  assert.equal(job.tool_policy.disabled_count, 1);
  const persisted = JSON.parse(readFileSync(job.report_path, 'utf8'));
  assert.deepEqual(persisted.tool_policy, job.tool_policy);
  assert.deepEqual((await call(session, 'corel_get_job', { job_id: job.job_id })).tool_policy, job.tool_policy);
});

test('invalid configuration rejects COM and local mutations without reserving jobs', async (t) => {
  const { connect, call } = await import('./helpers.mjs');
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-config-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  writeFileSync(join(root, 'config.json'), '{"disabled_tools":[],"unknown":true}');
  const runs = join(root, 'runs');
  const session = await connect({ runs, fixture: true, configRoot: root });
  t.after(() => session.close());
  const names = (await session.client.listTools()).tools.map(tool => tool.name);
  for (const name of ['corel_execute_vba', 'corel_export', 'corel_add_note', 'corel_prune_artifacts']) {
    assert(!names.includes(name));
    await assert.rejects(session.client.callTool({ name, arguments: {} }));
  }
  assert.deepEqual(readdirSync(runs), []);
  assert.equal((await call(session, 'corel_guide', {})).error, undefined);
  assert(names.includes('corel_preview'));
});
