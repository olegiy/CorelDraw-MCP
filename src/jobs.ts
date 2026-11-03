import { createHash, randomUUID } from 'node:crypto';
import { appendFileSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { AutomationFailure, type Arguments, type Executor, type Job } from './types.js';

function canonical(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (value && typeof value === 'object') return `{${Object.entries(value).sort(([a], [b]) => a.localeCompare(b)).map(([k, v]) => `${JSON.stringify(k)}:${canonical(v)}`).join(',')}}`;
  return JSON.stringify(value) ?? 'null';
}
function digest(value: string): string { return createHash('sha256').update(value).digest('hex'); }
export class JobManager {
  readonly session = randomUUID();
  private active: Job | undefined;
  private unpersisted = new Map<string, Job>();
  private closing = false;
  constructor(readonly root: string, private executor: Executor, private defaultDeadlineMs = 600_000) {
    mkdirSync(root, { recursive: true });
  }
  private save(job: Job): void {
    const temporary = `${job.report_path}.${this.session}.tmp`;
    writeFileSync(temporary, JSON.stringify(job, null, 2), 'utf8');
    renameSync(temporary, job.report_path);
  }
  private log(job: Job, message: string): void {
    appendFileSync(job.log_path, `${new Date().toISOString()} ${message}\n`, 'utf8');
  }
  private record(job: Job, message: string): void {
    const warn = (kind: string, error: unknown) => {
      const warning = `${kind}: ${error instanceof Error ? error.message : String(error)}`;
      if (!job.warnings.includes(warning)) job.warnings.push(warning);
    };
    try { this.log(job, message); } catch (error) { warn('Could not write job log', error); }
    try { this.save(job); this.unpersisted.delete(job.job_id); }
    catch (error) {
      warn('Could not persist report; query this server before restarting', error);
      this.unpersisted.set(job.job_id, job);
    }
  }
  submit(tool: string, args: Arguments): Job {
    if (this.closing) throw new AutomationFailure('SHUTTING_DOWN', 'The server is shutting down.');
    const requestId = typeof args.request_id === 'string' ? args.request_id : undefined;
    const fingerprint = digest(canonical({ tool, args }));
    const jobId = requestId ? `req-${digest(requestId)}` : randomUUID();
    const directory = join(this.root, jobId);
    const reportPath = join(directory, 'report.json');
    if (existsSync(directory) || this.unpersisted.has(jobId)) {
      const inMemory = this.unpersisted.get(jobId);
      if (inMemory) {
        if (inMemory.fingerprint !== fingerprint) throw new AutomationFailure('REQUEST_ID_CONFLICT', 'This request_id already belongs to different arguments.');
        return structuredClone(inMemory);
      }
      if (!existsSync(reportPath)) throw new AutomationFailure('BUSY', 'This request is being registered by another process; retry with the same request_id.');
      const previous = this.get(jobId);
      if (previous.fingerprint !== fingerprint) throw new AutomationFailure('REQUEST_ID_CONFLICT', 'This request_id already belongs to different arguments. Use the original arguments or a new request_id.');
      return previous;
    }
    if (this.active) throw new AutomationFailure('BUSY', `CorelDRAW is occupied by job ${this.active.job_id}; query corel_get_job before submitting another operation.`);
    try { mkdirSync(directory); } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'EEXIST') throw new AutomationFailure('BUSY', 'This request is being registered; retry the same request_id.');
      throw error;
    }
    const deadline = typeof args.deadline_seconds === 'number' ? args.deadline_seconds * 1000 : this.defaultDeadlineMs;
    const job: Job = {
      job_id: jobId, owner_pid: process.pid, owner_session: this.session, fingerprint, tool,
      status: 'running', stage: 'starting', started_at: new Date().toISOString(),
      deadline_at: new Date(Date.now() + deadline).toISOString(), deadline_exceeded: false,
      run_directory: directory, report_path: reportPath, log_path: join(directory, 'run.log'),
      artifacts: [], warnings: [], ...(requestId ? { request_id: requestId } : {}),
    };
    try {
      writeFileSync(join(directory, 'request.json'), JSON.stringify({ tool, args }, null, 2), 'utf8');
      this.log(job, `Submitted ${tool}`);
      this.save(job);
    } catch (error) {
      job.status = 'failed'; job.stage = 'registering'; job.finished_at = new Date().toISOString();
      job.error = { code: 'ARTIFACT_IO_ERROR', stage: job.stage, message: `No COM operation was dispatched: ${error instanceof Error ? error.message : String(error)}` };
      this.record(job, 'Registration failed before dispatch.');
      return structuredClone(job);
    }
    this.active = job;
    const timer = setTimeout(() => {
      job.status = 'timed_out';
      job.deadline_exceeded = true;
      job.warnings.push('The deadline expired. The COM call may still be running; do not repeat the operation.');
      this.record(job, 'Deadline exceeded; executor remains occupied.');
    }, deadline);
    void Promise.resolve().then(() => this.executor.run(job, args, stage => {
      job.stage = stage; this.record(job, stage);
    })).then(result => {
      job.status = result.ok ? 'succeeded' : 'failed';
      job.stage = result.ok ? 'completed' : (result.error?.stage ?? job.stage);
      if (result.result !== undefined) job.result = result.result;
      if (result.error) job.error = result.error;
      if (result.cleanup) job.cleanup = result.cleanup;
      if (result.compiler_location !== undefined) job.compiler_location = result.compiler_location;
      job.artifacts = result.artifacts ?? [];
      job.warnings.push(...(result.warnings ?? []));
    }).catch(error => {
      job.status = 'unknown';
      job.error = { code: 'EXECUTOR_LOST', stage: job.stage, message: `${error instanceof Error ? error.message : String(error)}. Inspect CorelDRAW before retrying with a new request_id.` };
    }).finally(() => {
      clearTimeout(timer); job.finished_at = new Date().toISOString();
      this.record(job, `Finished: ${job.status}`);
      this.active = undefined;
      if (this.closing) this.executor.close();
    });
    return structuredClone(job);
  }
  get(jobId: string): Job {
    if (!/^(?:[a-f0-9-]{36}|req-[a-f0-9]{64})$/.test(jobId)) throw new AutomationFailure('INVALID_JOB_ID', 'Use the job_id returned by an operation.');
    if (this.active?.job_id === jobId) return structuredClone(this.active);
    const unpersisted = this.unpersisted.get(jobId);
    if (unpersisted) return structuredClone(unpersisted);
    const file = join(this.root, jobId, 'report.json');
    if (!existsSync(file)) throw new AutomationFailure('JOB_NOT_FOUND', `Unknown job: ${jobId}`);
    const job = JSON.parse(readFileSync(file, 'utf8').replace(/^\uFEFF/, '')) as Job;
    if (['running', 'timed_out'].includes(job.status) && job.owner_session !== this.session) {
      try { process.kill(job.owner_pid, 0); } catch {
        job.status = 'unknown';
        job.error = { code: 'OWNER_LOST', stage: job.stage, message: 'The owning server exited. The executor or CorelDRAW may still be running; do not repeat this request.' };
      }
    }
    return job;
  }
  async wait(jobId: string, seconds: number): Promise<Job> {
    const until = Date.now() + seconds * 1000;
    let job = this.get(jobId);
    while (['running'].includes(job.status) && Date.now() < until) {
      await delay(Math.min(150, Math.max(1, until - Date.now()))); job = this.get(jobId);
    }
    return job;
  }
  close(): void {
    this.closing = true;
    // Let an in-flight COM mutation finish and persist its report even after stdin EOF.
    if (!this.active) this.executor.close();
  }
}
