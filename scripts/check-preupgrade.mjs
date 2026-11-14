import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseProtocolJson } from '../src/worker-protocol.ts';

const root = fileURLToPath(new URL('..', import.meta.url));
const jobPattern = /^(?:[a-f0-9-]{36}|req-[a-f0-9]{64})$/;
const decoder = new TextDecoder('utf-8', { fatal: true });
const proofKeys = ['job_id', 'tool', 'request_id', 'owner_pid', 'finished_at', 'run_directory', 'cleanup', 'artifacts'];
const canonical = value => JSON.stringify(sort(value));
function sort(value) {
  if (Array.isArray(value)) return value.map(sort);
  return value && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map(key => [key, sort(value[key])])) : value;
}
const binding = value => Object.fromEntries(proofKeys.filter(key => Object.hasOwn(value, key)).map(key => [key, value[key]]));
function noLinks(path) {
  for (let current = resolve(path);; current = dirname(current)) {
    try { if (lstatSync(current).isSymbolicLink()) throw new Error('UNSAFE_PATH'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (dirname(current) === current) break;
  }
}
function processExists(pid) {
  const result = spawnSync('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-File',
    join(root, 'scripts/Check-OwnerProcess.ps1'), '-ProcessId', String(pid)],
  { windowsHide: true, shell: false, encoding: 'utf8', timeout: 30000, maxBuffer: 65536 });
  if (result.error || result.status !== 0) return null;
  try { const value = JSON.parse(result.stdout.replace(/^\uFEFF/, '')); return typeof value.exists === 'boolean' ? value.exists : null; }
  catch { return null; }
}
function inspectHistory(path) {
  const python = join(root, 'webhost/.venv/Scripts/python.exe');
  if (!existsSync(python)) return { ok: false, reason: 'HISTORY_READER_UNAVAILABLE' };
  const result = spawnSync(python, ['-m', 'corel_agent.preupgrade', '--history', path],
    { cwd: join(root, 'webhost'), windowsHide: true, shell: false, encoding: 'utf8', timeout: 120000, maxBuffer: 16 * 1024**2,
      env: { ...process.env, PYTHONUTF8: '1' } });
  if (result.error) return { ok: false, reason: 'HISTORY_CHECK_INCOMPLETE' };
  try { const report = parseProtocolJson(result.stdout); return result.status === 0 && report.ok === true ? report : { ok: false, reason: report.reason ?? 'HISTORY_CHECK_INCOMPLETE' }; }
  catch { return { ok: false, reason: 'HISTORY_CHECK_INCOMPLETE' }; }
}

export function checkUpgrade({ reportRoots, hostRoots, processExists: alive = processExists, inspectHistory: history = inspectHistory }) {
  const snapshots = [], directories = [], owners = new Set(), resolved = [];
  let bytes = 0, reports = 0, histories = 0;
  const fail = reason => { throw new Error(reason); };
  function json(path) {
    noLinks(path); const info = statSync(path);
    if (!info.isFile() || info.size < 1 || info.size > 16 * 1024**2 || (bytes += info.size) > 512 * 1024**2) fail('CHECK_LIMIT');
    const data = readFileSync(path), digest = createHash('sha256').update(data).digest('hex');
    snapshots.push({ path, digest });
    return parseProtocolJson(decoder.decode(data));
  }
  function stopped(pid) {
    if (!Number.isInteger(pid) || pid < 1 || pid > 2147483647) fail('OWNER_UNCONFIRMED');
    if (owners.size > 1000 || alive(pid) !== false) fail('OWNER_NOT_STOPPED');
    owners.add(pid);
  }
  function entries(path) {
    noLinks(path);
    try {
      if (!statSync(path).isDirectory()) fail('ROOT_NOT_DIRECTORY');
      const names = readdirSync(path).sort();
      if (names.length > 100000) fail('CHECK_LIMIT');
      directories.push({ path, names }); return names;
    } catch (error) { if (error.code === 'ENOENT') { directories.push({ path, names: null }); return null; } throw error; }
  }
  try {
    for (const host of new Set(hostRoots.map(path => resolve(path)))) {
      const names = entries(host);
      if (names === null) continue;
      for (const name of ['runtime.json', 'mcp-owner.json']) if (names.includes(name)) {
        const marker = json(join(host, name));
        if (marker.phase !== 'running' || typeof marker.process?.creation_time !== 'string' || !marker.process.creation_time) fail('OWNER_UNCONFIRMED');
        stopped(marker.process.pid);
      }
      const path = join(host, 'history.sqlite3');
      if (names.includes('history.sqlite3')) {
        noLinks(path); const checked = history(path); histories++;
        if (checked.ok !== true) fail(checked.reason ?? 'HISTORY_CHECK_INCOMPLETE');
        resolved.push(...(checked.resolved_reports ?? []));
      }
    }
    for (const directory of new Set(reportRoots.map(path => resolve(path)))) {
      for (const name of entries(directory) ?? []) {
        const path = join(directory, name); noLinks(path); const info = lstatSync(path);
        if (!info.isDirectory()) continue;
        if (!jobPattern.test(name)) fail('UNKNOWN_REPORT_DIRECTORY');
        const report = json(join(path, 'report.json')); reports++;
        if (report.job_id !== name || typeof report.tool !== 'string' || resolve(report.run_directory ?? '') !== resolve(path)) fail('REPORT_UNCONFIRMED');
        if (!['succeeded', 'failed'].includes(report.status) || typeof report.finished_at !== 'string' || !report.finished_at) fail('JOB_UNRESOLVED');
        if (report.status === 'failed' && (report.error?.applied !== false || report.error?.partial === true || report.result?.partial === true)) fail('MUTATION_OUTCOME_UNRESOLVED');
        if (report.cleanup?.completed !== true || !Array.isArray(report.cleanup.errors) || report.cleanup.errors.length) fail('CLEANUP_NOT_PROVEN');
        if (report.cleanup.project_retained === true && !resolved.some(value => canonical(binding(value)) === canonical(binding(report)))) fail('RETAINED_PROJECT_UNRESOLVED');
        if (!owners.has(report.owner_pid)) stopped(report.owner_pid);
      }
    }
    for (const { path, digest } of snapshots) {
      noLinks(path);
      if (createHash('sha256').update(readFileSync(path)).digest('hex') !== digest) fail('EVIDENCE_CHANGED');
    }
    for (const { path, names } of directories) {
      noLinks(path);
      const current = existsSync(path) ? readdirSync(path).sort() : null;
      if (canonical(current) !== canonical(names)) fail('EVIDENCE_CHANGED');
    }
    for (const pid of owners) if (alive(pid) !== false) fail('OWNER_NOT_STOPPED');
    return { ok: true, reports, histories, owners_checked: owners.size };
  } catch (error) {
    const reason = /^[A-Z_]{1,80}$/.test(error.message) ? error.message : 'CHECK_INCOMPLETE';
    return { ok: false, reason, reports, histories, message: 'Обновление не допускается: прежний владелец, задания или полнота проверки не подтверждены. Исходные данные сохранены.' };
  }
}

export function defaultUpgradeOptions(environment = process.env) {
  if (!environment.LOCALAPPDATA) throw new Error('LOCALAPPDATA не определён; проверка перед обновлением неполна.');
  const host = join(environment.LOCALAPPDATA, 'CorelDrawAgent');
  const reportRoots = [join(environment.LOCALAPPDATA, 'CorelDrawMcp/runs'), join(host, 'runs')];
  if (environment.CORELDRAW_MCP_RUNS) reportRoots.push(environment.CORELDRAW_MCP_RUNS);
  return { reportRoots, hostRoots: [host] };
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const result = checkUpgrade(defaultUpgradeOptions());
    console.log(JSON.stringify(result)); process.exitCode = result.ok ? 0 : 1;
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
