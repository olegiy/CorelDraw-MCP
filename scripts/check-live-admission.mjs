import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { defaultUpgradeOptions } from './check-preupgrade.mjs';
import { parseProtocolJson } from '../src/worker-protocol.ts';

const repository = fileURLToPath(new URL('..', import.meta.url));
const canonical = value => JSON.stringify(sort(value));
function sort(value) {
  if (Array.isArray(value)) return value.map(sort);
  return value && typeof value === 'object'
    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, sort(value[key])])) : value;
}
const hash = value => createHash('sha256').update(value).digest('hex');
const utf8 = new TextDecoder('utf-8', { fatal: true });
function requireCheckpoint(path) {
  for (const suffix of ['-wal', '-journal']) {
    const journal = path + suffix; noLinks(journal);
    if (existsSync(journal) && statSync(journal).size) throw new Error('HISTORY_NOT_CHECKPOINTED');
  }
}
function noLinks(path) {
  for (let current = resolve(path);; current = dirname(current)) {
    try { if (lstatSync(current).isSymbolicLink()) throw new Error('UNSAFE_PATH'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (dirname(current) === current) break;
  }
}
function ownerStopped(pid) {
  const result = spawnSync('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-File',
    join(repository, 'scripts/Check-OwnerProcess.ps1'), '-ProcessId', String(pid)],
  { windowsHide: true, shell: false, encoding: 'utf8', timeout: 30000, maxBuffer: 65536 });
  if (result.error || result.status !== 0) return null;
  try { return parseProtocolJson(result.stdout).exists; } catch { return null; }
}
function inspectHistory(path) {
  const result = spawnSync(join(repository, 'webhost/.venv/Scripts/python.exe'),
    ['-m', 'corel_agent.preupgrade', '--history', path],
    { cwd: join(repository, 'webhost'), windowsHide: true, shell: false, encoding: 'utf8',
      timeout: 120000, maxBuffer: 16 * 1024**2, env: { ...process.env, PYTHONUTF8: '1' } });
  if (result.error || ![0, 1].includes(result.status)) throw new Error('HISTORY_CHECK_INCOMPLETE');
  return parseProtocolJson(result.stdout);
}

export function captureLiveAdmission({ reportRoots, hostRoots, processExists = ownerStopped,
  inspectHistory: history = inspectHistory }) {
  const files = [], directories = [], histories = [], historical_exceptions = [], owners = new Set();
  let total = 0;
  const roots = { reportRoots: [...new Set(reportRoots.map(path => resolve(path)))].sort(),
    hostRoots: [...new Set(hostRoots.map(path => resolve(path)))].sort() };
  function read(path, maximum = 16 * 1024**2) {
    noLinks(path);
    const info = statSync(path);
    if (!info.isFile() || info.size < 1 || info.size > maximum || (total += info.size) > 512 * 1024**2)
      throw new Error('CHECK_LIMIT');
    const data = readFileSync(path);
    files.push({ path, sha256: hash(data) });
    return data;
  }
  function entries(path) {
    noLinks(path);
    const names = existsSync(path) ? readdirSync(path).sort() : null;
    if (names?.length > 100000) throw new Error('CHECK_LIMIT');
    directories.push({ path, names });
    return names ?? [];
  }
  function stopped(pid) {
    if (!Number.isInteger(pid) || pid < 1 || pid > 2147483647 || owners.size > 1000)
      throw new Error('OWNER_UNCONFIRMED');
    if (processExists(pid) !== false) throw new Error('OWNER_NOT_STOPPED');
    owners.add(pid);
  }
  for (const host of roots.hostRoots) {
    const names = entries(host);
    for (const name of ['runtime.json', 'mcp-owner.json']) if (names.includes(name)) {
      const marker = parseProtocolJson(utf8.decode(read(join(host, name))));
      if (marker.phase !== 'running' || typeof marker.process?.creation_time !== 'string' || !marker.process.creation_time)
        throw new Error('OWNER_UNCONFIRMED');
      stopped(marker.process.pid);
    }
    if (!names.includes('history.sqlite3')) continue;
    const path = join(host, 'history.sqlite3');
    histories.push(path);
    requireCheckpoint(path);
    read(path, 256 * 1024**2);
    const result = history(path);
    if (result.ok !== true) {
      if (result.ok !== false || result.reason !== 'MUTATION_OUTCOME_UNRESOLVED')
        throw new Error(result.reason ?? 'HISTORY_CHECK_INCOMPLETE');
      historical_exceptions.push({ path, reason: result.reason });
    }
  }
  for (const root of roots.reportRoots) for (const name of entries(root)) {
    const directory = join(root, name); noLinks(directory);
    if (!lstatSync(directory).isDirectory()) continue;
    if (!/^(?:[a-f0-9-]{36}|req-[a-f0-9]{64})$/.test(name)) throw new Error('UNKNOWN_REPORT_DIRECTORY');
    const path = join(directory, 'report.json');
    const report = parseProtocolJson(utf8.decode(read(path)));
    if (report.job_id !== name || typeof report.tool !== 'string' || typeof report.run_directory !== 'string'
      || resolve(report.run_directory) !== directory) throw new Error('REPORT_UNCONFIRMED');
    if (!['succeeded', 'failed'].includes(report.status) || typeof report.finished_at !== 'string' || !report.finished_at)
      throw new Error('JOB_UNRESOLVED');
    if (report.cleanup?.completed !== true || !Array.isArray(report.cleanup.errors) || report.cleanup.errors.length)
      throw new Error('CLEANUP_NOT_PROVEN');
    if (!owners.has(report.owner_pid)) stopped(report.owner_pid);
    if ((report.status === 'failed' && (report.error?.applied !== false || report.error?.partial === true
      || report.result?.partial === true)) || report.cleanup.project_retained === true)
      historical_exceptions.push({ path, job_id: name, tool: report.tool, status: report.status,
        reason: 'ACCEPTED_HISTORICAL_UNCERTAINTY', project_retained: report.cleanup.project_retained === true });
  }
  for (const file of files) {
    noLinks(file.path);
    if (hash(readFileSync(file.path)) !== file.sha256) throw new Error('EVIDENCE_CHANGED');
  }
  for (const directory of directories) {
    noLinks(directory.path);
    const current = existsSync(directory.path) ? readdirSync(directory.path).sort() : null;
    if (canonical(current) !== canonical(directory.names)) throw new Error('EVIDENCE_CHANGED');
  }
  for (const pid of owners) if (processExists(pid) !== false) throw new Error('OWNER_NOT_STOPPED');
  for (const path of histories) requireCheckpoint(path);
  const snapshot = { schema_version: 1, purpose: 'owned-fixture-live-tests', roots, files,
    directories, historical_exceptions, owners_checked: owners.size };
  return { ...snapshot, accepted_at: new Date().toISOString(), snapshot_sha256: hash(canonical(snapshot)) };
}

export function verifyLiveAdmission(receipt, options) {
  try {
    const { accepted_at, snapshot_sha256, ...snapshot } = receipt;
    if (snapshot.schema_version !== 1 || snapshot.purpose !== 'owned-fixture-live-tests'
      || typeof accepted_at !== 'string' || !Number.isFinite(Date.parse(accepted_at))
      || hash(canonical(snapshot)) !== snapshot_sha256) throw new Error('RECEIPT_INVALID');
    const current = captureLiveAdmission(options);
    if (current.snapshot_sha256 !== snapshot_sha256) throw new Error('SNAPSHOT_CHANGED');
    return { ok: true, purpose: snapshot.purpose, historical_exceptions: current.historical_exceptions.length,
      snapshot_sha256, owners_checked: current.owners_checked };
  } catch (error) {
    return { ok: false, reason: /^[A-Z_]+$/.test(error.message) ? error.message : 'LIVE_ADMISSION_INCOMPLETE' };
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const [mode, destination, ...extra] = process.argv.slice(2);
    if (!destination || extra.length || !['--accept-historical', '--receipt'].includes(mode))
      throw new Error('Нужен --accept-historical <новый receipt.json> либо --receipt <receipt.json>.');
    const options = defaultUpgradeOptions();
    noLinks(destination);
    if (mode === '--accept-historical') {
      const receipt = captureLiveAdmission(options);
      writeFileSync(destination, JSON.stringify(receipt, null, 2), { flag: 'wx' });
      console.log(JSON.stringify({ ok: true, purpose: receipt.purpose, snapshot_sha256: receipt.snapshot_sha256,
        historical_exceptions: receipt.historical_exceptions.length, receipt: resolve(destination) }));
    } else {
      if (statSync(destination).size > 16 * 1024**2) throw new Error('CHECK_LIMIT');
      const result = verifyLiveAdmission(parseProtocolJson(utf8.decode(readFileSync(destination))), options);
      console.log(JSON.stringify(result)); process.exitCode = result.ok ? 0 : 1;
    }
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
