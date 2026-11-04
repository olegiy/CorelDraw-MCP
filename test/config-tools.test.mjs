import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

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
