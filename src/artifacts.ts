import { createHash, randomUUID } from 'node:crypto';
import {
  closeSync,
  existsSync,
  linkSync,
  lstatSync,
  mkdirSync,
  openSync,
  readdirSync,
  readSync,
  renameSync,
  rmdirSync,
  statSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { AutomationFailure, type Job } from './types.js';

const validId = /^(?:[a-f0-9-]{36}|req-[a-f0-9]{64})$/;
export function assertNoLinks(path: string): void {
  let current = resolve(path);
  for (;;) {
    if (existsSync(current) && lstatSync(current).isSymbolicLink())
      throw new AutomationFailure('REPARSE_POINT', 'Переход через ссылку или junction запрещён.');
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }
}
export function atomicJson(path: string, value: unknown, overwrite = true): void {
  assertNoLinks(dirname(path));
  if (existsSync(path) && lstatSync(path).isSymbolicLink())
    throw new AutomationFailure('REPARSE_POINT', 'Файл назначения является ссылкой.');
  if (!overwrite && existsSync(path)) throw new AutomationFailure('FILE_EXISTS', 'Файл уже существует.');
  const temp = join(dirname(path), `.corelmcp-${randomUUID()}.tmp`);
  try {
    writeFileSync(temp, JSON.stringify(value, null, 2), { encoding: 'utf8', flag: 'wx' });
    if (!overwrite) {
      // Atomic no-replace publication on the same filesystem: no empty placeholder.
      linkSync(temp, path);
    } else {
      renameSync(temp, path);
    }
  } finally {
    if (existsSync(temp)) unlinkSync(temp);
  }
}

type Entry = { path: string; relative: string; directory: boolean; bytes: number; digest: string };
function inventory(directory: string): { entries: Entry[]; bytes: number; token: string } {
  assertNoLinks(directory);
  const entries: Entry[] = [];
  let bytes = 0;
  const visit = (base: string, depth: number): void => {
    if (depth > 32) throw new AutomationFailure('ARTIFACT_LIMIT', 'Превышена глубина каталогов артефактов.');
    for (const name of readdirSync(base).sort()) {
      if (name === '.prune-lock' && base === directory) continue;
      if (entries.length >= 10000)
        throw new AutomationFailure('ARTIFACT_LIMIT', 'Слишком много файлов артефактов.');
      const path = join(base, name);
      const info = lstatSync(path);
      if (info.isSymbolicLink())
        throw new AutomationFailure('REPARSE_POINT', 'Каталог содержит ссылку или junction.');
      if (!info.isDirectory() && !info.isFile())
        throw new AutomationFailure('ARTIFACT_TYPE', 'Неподдерживаемый тип артефакта.');
      let digest = '';
      if (info.isFile()) {
        const hash = createHash('sha256');
        const fd = openSync(path, 'r');
        const buffer = Buffer.alloc(65536);
        try {
          for (;;) {
            const n = readSync(fd, buffer, 0, buffer.length, null);
            if (!n) break;
            hash.update(buffer.subarray(0, n));
          }
        } finally {
          closeSync(fd);
        }
        digest = hash.digest('hex');
        bytes += info.size;
      }
      entries.push({
        path,
        relative: relative(directory, path),
        directory: info.isDirectory(),
        bytes: info.size,
        digest,
      });
      if (info.isDirectory()) visit(path, depth + 1);
    }
  };
  visit(directory, 0);
  const token = createHash('sha256')
    .update(JSON.stringify(entries.map((e) => [e.relative, e.directory, e.bytes, e.digest])))
    .digest('hex');
  return { entries, bytes, token };
}
function protectedReasons(job: Job): string[] {
  const reasons: string[] = [];
  if (job.status === 'unknown') reasons.push('UNKNOWN_OUTCOME');
  if (job.status === 'running' || job.status === 'timed_out' || !job.finished_at) reasons.push('UNFINISHED');
  if (job.cleanup?.completed === false) reasons.push('CLEANUP_INCOMPLETE');
  if (job.cleanup?.project_retained === true) reasons.push('PROJECT_RETAINED');
  return reasons;
}
const code = (value: unknown): string | undefined =>
  typeof value === 'string' && /^[A-Z][A-Z0-9_]{0,99}$/.test(value) ? value : undefined;
const version = (value: unknown): string =>
  typeof value === 'string' && /^v?\d+(?:\.\d+){0,3}(?:-[a-z0-9.-]+)?$/i.test(value) ? value : 'нет данных';
export function diagnosticSummary(job: Job): Record<string, any> {
  const errors = Array.isArray(job.cleanup?.errors) ? (job.cleanup.errors as Record<string, unknown>[]) : [];
  return {
    diagnostic_id: createHash('sha256').update(job.job_id).digest('hex'),
    tool: /^corel_[a-z_]+$/.test(job.tool) ? job.tool : 'нет данных',
    status: ['running', 'timed_out', 'succeeded', 'failed', 'unknown'].includes(job.status)
      ? job.status
      : 'unknown',
    stage: /^[a-z_]{1,64}$/.test(job.stage) ? job.stage : 'нет данных',
    started_at: validTime(job.started_at),
    finished_at: validTime(job.finished_at),
    deadline_exceeded: job.deadline_exceeded === true,
    versions: {
      server: version(job.versions?.server),
      node: version(job.versions?.node),
      powershell: version(job.versions?.powershell),
      coreldraw: version(job.versions?.coreldraw),
    },
    error: job.error
      ? {
           code: code(job.error.code) ?? 'UNSPECIFIED_ERROR',
           ...(typeof job.error.applied === 'boolean' ? { applied: job.error.applied } : {}),
          ...(Number.isSafeInteger(job.error.number) ? { number: job.error.number } : {}),
        }
      : null,
    cleanup: {
      completed: typeof job.cleanup?.completed === 'boolean' ? job.cleanup.completed : 'нет данных',
      project_retained: typeof job.cleanup?.project_retained === 'boolean' ? job.cleanup.project_retained : 'нет данных',
      errors: errors.map((e) => ({ code: code(e.code) ?? 'CLEANUP_FAILED' })),
    },
    details_pruned: job.details_pruned === true,
  };
}
function validTime(value: unknown): string | null {
  return typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(value) ? value : null;
}

export class ArtifactStore {
  constructor(
    readonly root: string,
    private getJob: (id: string) => Job,
  ) {}
  list(offset = 0, limit = 50) {
    assertNoLinks(this.root);
    const names = readdirSync(this.root)
      .filter((name) => validId.test(name))
      .sort();
    const items = names.slice(offset, offset + limit).map((jobId) => {
      const directory = join(this.root, jobId);
      let bytes = 0,
        token = '',
        reasons: string[] = [],
        age: number | null = null,
        pruned = false;
      try {
        const job = this.getJob(jobId);
        reasons = protectedReasons(job);
        pruned = job.details_pruned === true;
        const date = Date.parse(job.finished_at ?? job.started_at);
        if (Number.isFinite(date)) age = Math.max(0, Math.floor((Date.now() - date) / 1000));
        const files = inventory(directory);
        bytes = files.bytes;
        token = files.token;
      } catch (error) {
        reasons.push(error instanceof AutomationFailure ? error.code : 'ARTIFACT_IO_ERROR');
      }
      return {
        job_id: jobId,
        bytes,
        age_seconds: age,
        protected: reasons.length > 0,
        protection_reasons: reasons,
        prune_token: token || null,
        details_pruned: pruned,
      };
    });
    return { items, total: names.length, next_offset: offset + limit < names.length ? offset + limit : null };
  }
  prune(candidates: { job_id: string; prune_token: string }[]) {
    const items = candidates.map((candidate) => {
      const id = candidate.job_id;
      let locked = false;
      if (!validId.test(id)) return { job_id: id, pruned: false, code: 'INVALID_JOB_ID' };
      const directory = join(this.root, id);
      const lock = join(directory, '.prune-lock');
      try {
        assertNoLinks(directory);
        mkdirSync(lock);
        locked = true;
        const files = inventory(directory);
        const job = this.getJob(id);
        const reasons = protectedReasons(job);
        if (reasons.length) return { job_id: id, pruned: false, code: 'ARTIFACTS_PROTECTED', reasons };
        if (files.token !== candidate.prune_token)
          return { job_id: id, pruned: false, code: 'ARTIFACTS_CHANGED' };
        const report = join(directory, 'report.json');
        const compact: Job = {
          job_id: job.job_id,
          owner_pid: job.owner_pid,
          owner_session: job.owner_session,
          fingerprint: job.fingerprint,
          tool: job.tool,
          status: job.status,
          stage: job.stage,
          started_at: job.started_at,
          ...(job.finished_at ? { finished_at: job.finished_at } : {}),
          deadline_at: job.deadline_at,
          deadline_exceeded: job.deadline_exceeded,
          run_directory: directory,
          report_path: report,
          log_path: join(directory, 'run.log'),
          artifacts: [],
          warnings: [],
          details_pruned: true,
          ...(job.tool_policy ? { tool_policy: job.tool_policy } : {}),
          ...(job.request_id ? { request_id: job.request_id } : {}),
          ...(job.versions ? { versions: job.versions } : {}),
          ...(job.error
            ? {
                error: {
                   code: job.error.code,
                   ...(typeof job.error.applied === 'boolean' ? { applied: job.error.applied } : {}),
                  stage: job.error.stage,
                  message: 'Подробности удалены по явной команде.',
                  ...(job.error.number !== undefined ? { number: job.error.number } : {}),
                },
              }
            : {}),
          cleanup: {
            ...(typeof job.cleanup?.completed === 'boolean' ? {completed: job.cleanup.completed} : {}),
            ...(typeof job.cleanup?.project_retained === 'boolean' ? {project_retained: job.cleanup.project_retained} : {}),
            errors: Array.isArray(job.cleanup?.errors)
              ? (job.cleanup.errors as Record<string, unknown>[]).map((error) => ({
                  code: code(error.code) ?? 'CLEANUP_FAILED',
                }))
              : [],
          },
          pruned_inventory: { token: files.token, bytes: files.bytes },
        };
        // Preserve the reservation and compact outcome BEFORE removing any source.
        atomicJson(report, compact);
        for (const entry of [...files.entries].reverse()) {
          if (entry.path === report) continue;
          assertNoLinks(entry.path);
          const rel = relative(directory, entry.path);
          if (isAbsolute(rel) || rel === '..' || rel.startsWith(`..${sep}`))
            throw new AutomationFailure('ARTIFACT_PATH', 'Артефакт вне каталога задания.');
          if (entry.directory) rmdirSync(entry.path);
          else unlinkSync(entry.path);
        }
        return { job_id: id, pruned: true, freed_bytes: Math.max(0, files.bytes - statSync(report).size) };
      } catch (error) {
        return {
          job_id: id,
          pruned: false,
          code:
            error instanceof AutomationFailure
              ? error.code
              : (error as NodeJS.ErrnoException).code === 'EEXIST'
                ? 'PRUNE_BUSY'
                : 'ARTIFACT_IO_ERROR',
        };
      } finally {
        if (locked) {
          try {
            rmdirSync(lock);
          } catch {
            /* A leftover lock conservatively prevents concurrent cleanup. */
          }
        }
      }
    });
    return { items };
  }
}
