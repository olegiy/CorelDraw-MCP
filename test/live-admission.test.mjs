import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { captureLiveAdmission, verifyLiveAdmission } from '../scripts/check-live-admission.mjs';
import { checkUpgrade } from '../scripts/check-preupgrade.mjs';

function fixture() {
  const root = mkdtempSync(join(tmpdir(), 'corel-live-admission-'));
  const reports = join(root, 'runs'), host = join(root, 'host');
  mkdirSync(reports); mkdirSync(host);
  const job = 'req-' + 'a'.repeat(64), directory = join(reports, job);
  mkdirSync(directory);
  const path = join(directory, 'report.json');
  const report = { job_id: job, tool: 'corel_execute_vba', owner_pid: 123,
    status: 'failed', finished_at: '2026-11-09T00:00:00Z', run_directory: directory,
    error: { code: 'COMPILATION_FAILED' }, cleanup: { completed: true, errors: [], project_retained: true } };
  writeFileSync(path, JSON.stringify(report));
  writeFileSync(join(host, 'history.sqlite3'), 'synthetic history');
  const options = { reportRoots: [reports], hostRoots: [host], processExists: () => false,
    inspectHistory: () => ({ ok: false, reason: 'MUTATION_OUTCOME_UNRESOLVED' }) };
  return { root, path, report, options };
}

test('explicit live acceptance preserves historical uncertainty and never admits an upgrade', () => {
  const f = fixture(), before = readFileSync(f.path), receipt = captureLiveAdmission(f.options);
  assert.equal(receipt.purpose, 'owned-fixture-live-tests');
  assert.equal(receipt.historical_exceptions.length, 2);
  assert.equal(verifyLiveAdmission(receipt, f.options).ok, true);
  assert.equal(checkUpgrade(f.options).ok, false);
  assert.deepEqual(readFileSync(f.path), before);
  assert.equal(JSON.parse(before).error.applied, undefined);
});

test('legacy completed cleanup without errors is recognized without resolving the mutation', () => {
  const f = fixture();
  delete f.report.cleanup.errors;
  writeFileSync(f.path, JSON.stringify(f.report));
  const before = readFileSync(f.path);
  const receipt = captureLiveAdmission(f.options);
  assert.equal(receipt.historical_exceptions.length, 2);
  assert.equal(verifyLiveAdmission(receipt, f.options).ok, true);
  assert.equal(checkUpgrade(f.options).ok, false);
  assert.deepEqual(readFileSync(f.path), before);
});

test('missing current cleanup errors and explicit malformed or nonempty errors remain blocked', () => {
  for (const extra of [{ versions: {} }, { errors: null }, { errors: ['failure'] }]) {
    const f = fixture();
    delete f.report.cleanup.errors;
    if (Object.hasOwn(extra, 'versions')) Object.assign(f.report, extra);
    else Object.assign(f.report.cleanup, extra);
    writeFileSync(f.path, JSON.stringify(f.report));
    assert.throws(() => captureLiveAdmission(f.options), /CLEANUP_NOT_PROVEN/);
  }
});

test('changed report or new job cannot inherit historical live acceptance', () => {
  for (const change of ['bytes', 'directory']) {
    const f = fixture(), receipt = captureLiveAdmission(f.options);
    if (change === 'bytes') writeFileSync(f.path, JSON.stringify({ ...f.report, error: { code: 'OTHER' } }));
    else mkdirSync(join(f.options.reportRoots[0], 'new-job'));
    assert.equal(verifyLiveAdmission(receipt, f.options).ok, false);
  }
});

test('purpose digest history or roots tampering invalidates acceptance', () => {
  for (const change of ['purpose', 'digest', 'history', 'roots']) {
    const f = fixture(), receipt = captureLiveAdmission(f.options);
    if (change === 'purpose') receipt.purpose = 'upgrade';
    if (change === 'digest') receipt.snapshot_sha256 = '0'.repeat(64);
    if (change === 'history') writeFileSync(join(f.options.hostRoots[0], 'history.sqlite3'), 'changed');
    if (change === 'roots') f.options.reportRoots = [];
    assert.equal(verifyLiveAdmission(receipt, f.options).ok, false);
  }
});

test('new active or unverified owner blocks an unchanged accepted snapshot', () => {
  for (const value of [true, null]) {
    const f = fixture(), receipt = captureLiveAdmission(f.options);
    assert.equal(verifyLiveAdmission(receipt, { ...f.options, processExists: () => value }).ok, false);
  }
});

test('owners are verified again after collecting evidence', () => {
  const f = fixture(); let calls = 0;
  assert.throws(() => captureLiveAdmission({ ...f.options, processExists: () => ++calls === 1 ? false : true }), /OWNER/);
});

test('unfinished reports cleanup and unverified history cannot be accepted', () => {
  for (const status of ['running', 'unknown', 'timed_out']) {
    const f = fixture(); writeFileSync(f.path, JSON.stringify({ ...f.report, status }));
    assert.throws(() => captureLiveAdmission(f.options), /JOB_UNRESOLVED/);
  }
  const f = fixture();
  writeFileSync(f.path, JSON.stringify({ ...f.report, cleanup: { completed: false, errors: [] } }));
  assert.throws(() => captureLiveAdmission(f.options), /CLEANUP/);
  const clean = fixture();
  assert.throws(() => captureLiveAdmission({ ...clean.options,
    inspectHistory: () => ({ ok: false, reason: 'ACTIVE_RUN' }) }), /ACTIVE_RUN/);
});

test('journals and unfinished host markers block acceptance', () => {
  for (const name of ['history.sqlite3-wal', 'mcp-owner.json']) {
    const f = fixture(); writeFileSync(join(f.options.hostRoots[0], name), name === 'mcp-owner.json' ? '{"phase":"spawn_pending"}' : 'pending');
    assert.throws(() => captureLiveAdmission(f.options));
  }
});

test('history changes during scanning and dangling history links cannot be accepted', () => {
  const f = fixture();
  assert.throws(() => captureLiveAdmission({ ...f.options, inspectHistory: () => {
    writeFileSync(join(f.options.hostRoots[0], 'history.sqlite3'), 'changed during read');
    return { ok: true };
  } }), /EVIDENCE_CHANGED/);
  const linked = fixture();
  symlinkSync(join(linked.root, 'absent'), join(linked.options.hostRoots[0], 'mcp-owner.json'),
    process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => captureLiveAdmission(linked.options), /UNSAFE_PATH/);
});

test('an existing empty journal becoming nonempty after evidence scans blocks live admission', () => {
  for (const suffix of ['-wal', '-journal']) {
    const f = fixture(), journal = join(f.options.hostRoots[0], 'history.sqlite3' + suffix);
    writeFileSync(journal, '');
    let checks = 0;
    assert.throws(() => captureLiveAdmission({ ...f.options, processExists: () => {
      if (++checks === 2) writeFileSync(journal, 'uncheckpointed transaction');
      return false;
    } }), /HISTORY_NOT_CHECKPOINTED/);
  }
});

test('invalid UTF-8 reports or owner markers cannot be normalized into accepted evidence', () => {
  for (const kind of ['report', 'marker']) {
    const f = fixture();
    const path = kind === 'report' ? f.path : join(f.options.hostRoots[0], 'mcp-owner.json');
    const value = kind === 'report' ? { ...f.report, result: { note: 'BADBYTE' } }
      : { phase: 'running', process: { pid: 123, creation_time: 'fixture-time' }, note: 'BADBYTE' };
    const bytes = Buffer.from(JSON.stringify(value));
    bytes[bytes.indexOf('BADBYTE')] = 0xff;
    writeFileSync(path, bytes);
    assert.throws(() => captureLiveAdmission(f.options));
    assert.deepEqual(readFileSync(path), bytes);
  }
});
