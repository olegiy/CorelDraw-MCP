import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { checkUpgrade } from '../scripts/check-preupgrade.mjs';

function fixture(status = 'succeeded', update = {}) {
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-upgrade-'));
  const id = 'req-' + 'a'.repeat(64), directory = join(root, id);
  mkdirSync(directory);
  const report = { job_id: id, tool: 'corel_execute_vba', owner_pid: 123,
    request_id: 'historical', status, finished_at: '2026-11-10T00:00:00Z', run_directory: directory,
    cleanup: { completed: true, errors: [] }, ...update };
  const path = join(directory, 'report.json'); writeFileSync(path, JSON.stringify(report));
  const options = { reportRoots: [root], hostRoots: [], processExists: () => false };
  return { root, id, path, report, options };
}

test('upgrade admission preserves confirmed legacy reports byte for byte', () => {
  const f = fixture(); const before = readFileSync(f.path);
  assert.equal(checkUpgrade(f.options).ok, true);
  assert.deepEqual(readFileSync(f.path), before);
});
for (const status of ['running', 'timed_out', 'unknown']) test(`upgrade refuses ${status} without replay or changing reports`, () => {
  const f = fixture(status), before = readFileSync(f.path);
  assert.equal(checkUpgrade(f.options).ok, false);
  assert.deepEqual(readFileSync(f.path), before);
});
test('failed is admissible only with a proven unapplied and nonpartial outcome', () => {
  for (const error of [{}, { applied: null }, { applied: true }, { applied: false, partial: true }]) {
    const f = fixture('failed', { error }), before = readFileSync(f.path);
    assert.equal(checkUpgrade(f.options).ok, false, JSON.stringify(error));
    assert.deepEqual(readFileSync(f.path), before);
  }
  const f = fixture('failed', { error: { applied: false, partial: false } });
  assert.equal(checkUpgrade(f.options).ok, true);
  const partial = fixture('failed', { error: { applied: false }, result: { partial: true, created_static_ids: [42] } });
  assert.equal(checkUpgrade(partial.options).ok, false);
});
test('upgrade refuses live or unverified owners, incomplete scans and retained GMS', () => {
  const f = fixture();
  for (const alive of [true, null]) assert.equal(checkUpgrade({ ...f.options, processExists: () => alive }).ok, false);
  mkdirSync(join(f.root, 'unexpected-directory')); assert.equal(checkUpgrade(f.options).ok, false);
  const retained = fixture('failed', { cleanup: { completed: true, errors: [], project_retained: true } });
  assert.equal(checkUpgrade(retained.options).ok, false);
});
test('history proof resolves only the exact retained report and cannot bypass a changed report', () => {
  const f = fixture('failed', { error: { applied: false }, cleanup: { completed: true, errors: [], project_retained: true, project_name: 'CorelMcp_a123456789ab' } });
  const host = join(f.root, '..', 'host-' + f.id.slice(0, 10) + '-' + Date.now()); mkdirSync(host);
  writeFileSync(join(host, 'history.sqlite3'), 'fixture');
  const options = { ...f.options, hostRoots: [host], inspectHistory: () => ({ ok: true, resolved_reports: [f.report] }) };
  assert.equal(checkUpgrade(options).ok, true);
  const changed = { ...f.report, cleanup: { ...f.report.cleanup, project_name: 'CorelMcp_changed' } };
  writeFileSync(f.path, JSON.stringify(changed)); assert.equal(checkUpgrade(options).ok, false);
});
test('upgrade refuses an unfinished host marker even with otherwise clean reports', () => {
  const f = fixture(), host = join(f.root, '..', 'host-marker-' + Date.now()); mkdirSync(host);
  writeFileSync(join(host, 'mcp-owner.json'), JSON.stringify({ phase: 'spawn_pending' }));
  assert.equal(checkUpgrade({ ...f.options, hostRoots: [host] }).ok, false);
});
for (const name of ['mcp-owner.json', 'history.sqlite3']) test(`a dangling ${name} is incomplete evidence, not absence`, () => {
  const f = fixture(), host = join(f.root, 'host'); mkdirSync(host);
  symlinkSync(join(host, 'missing'), join(host, name), process.platform === 'win32' ? 'junction' : 'dir');
  assert.equal(checkUpgrade({ ...f.options, reportRoots: [], hostRoots: [host] }).ok, false);
});
