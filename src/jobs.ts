import { createHash, randomUUID } from 'node:crypto';
import { appendFileSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { AutomationFailure, type Arguments, type Executor, type Job } from './types.js';
import { assertNoLinks } from './artifacts.js';
import { VERSION } from './version.js';

function canonical(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (value && typeof value === 'object')
    return `{${Object.entries(value)
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([k, v]) => `${JSON.stringify(k)}:${canonical(v)}`)
      .join(',')}}`;
  return JSON.stringify(value) ?? 'null';
}
function digest(value: string): string {
  return createHash('sha256').update(value).digest('hex');
}
export class JobManager {
  readonly session = randomUUID();
  private active: Job | undefined;
  private unpersisted = new Map<string, Job>();
  private closing = false;
  constructor(
    readonly root: string,
    private executor: Executor,
    private defaultDeadlineMs = 600_000,
  ) {
    assertNoLinks(root);
    mkdirSync(root, { recursive: true });
  }
  submitLocal(
    tool: string,
    args: Arguments,
    handler: (job: Job) => { result?: unknown; error?: Job['error']; warnings?: string[] },
  ): Job {
    if (this.closing) throw new AutomationFailure('SHUTTING_DOWN', 'Сервер завершает работу.');
    assertNoLinks(this.root);
    const requestId = typeof args.request_id === 'string' ? args.request_id : undefined;
    if (!requestId)
      throw new AutomationFailure('REQUEST_ID_REQUIRED', 'Локальная операция требует request_id.');
    const fingerprint = digest(canonical({ tool, args }));
    const jobId = `req-${digest(requestId)}`;
    const directory = join(this.root, jobId);
    const reportPath = join(directory, 'report.json');
    const inMemory = this.unpersisted.get(jobId);
    if (inMemory) {
      if (inMemory.fingerprint !== fingerprint)
        throw new AutomationFailure(
          'REQUEST_ID_CONFLICT',
          'Этот request_id уже связан с другими параметрами.',
        );
      return structuredClone(inMemory);
    }
    if (existsSync(reportPath)) {
      const previous = this.get(jobId);
      if (previous.fingerprint !== fingerprint)
        throw new AutomationFailure(
          'REQUEST_ID_CONFLICT',
          'Этот request_id уже связан с другими параметрами.',
        );
      return previous;
    }
    try {
      mkdirSync(directory);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'EEXIST')
        throw new AutomationFailure('BUSY', 'Запрос уже регистрируется; повторите тот же request_id.');
      throw error;
    }
    const now = new Date().toISOString();
    const job: Job = {
      job_id: jobId,
      owner_pid: process.pid,
      owner_session: this.session,
      request_id: requestId,
      fingerprint,
      tool,
      status: 'running',
      stage: 'local',
      started_at: now,
      deadline_at: now,
      deadline_exceeded: false,
      run_directory: directory,
      report_path: reportPath,
      log_path: join(directory, 'run.log'),
      artifacts: [],
      warnings: [],
      cleanup: { completed: true, errors: [] },
    };
    job.versions = { server: VERSION, node: process.versions.node };
    this.save(job);
    try {
      const outcome = handler(job);
      job.status = outcome.error ? 'failed' : 'succeeded';
      job.stage = outcome.error?.stage ?? 'completed';
      if (outcome.result !== undefined) job.result = outcome.result;
      if (outcome.error) job.error = outcome.error;
      job.warnings.push(...(outcome.warnings ?? []));
    } catch (error) {
      job.status = 'failed';
      job.error = {
        code: error instanceof AutomationFailure ? error.code : 'LOCAL_OPERATION_FAILED',
        stage: 'local',
        message: error instanceof Error ? error.message : String(error),
      };
    }
    job.finished_at = new Date().toISOString();
    this.record(job, `Завершено: ${job.status}`);
    return structuredClone(job);
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
    try {
      this.log(job, message);
    } catch (error) {
      warn('Не удалось записать журнал задания', error);
    }
    try {
      this.save(job);
      this.unpersisted.delete(job.job_id);
    } catch (error) {
      warn('Не удалось сохранить отчёт; опросите этот сервер до перезапуска', error);
      this.unpersisted.set(job.job_id, job);
    }
  }
  submit(tool: string, args: Arguments): Job {
    assertNoLinks(this.root);
    if (this.closing) throw new AutomationFailure('SHUTTING_DOWN', 'Сервер завершает работу.');
    const requestId = typeof args.request_id === 'string' ? args.request_id : undefined;
    const fingerprint = digest(canonical({ tool, args }));
    const jobId = requestId ? `req-${digest(requestId)}` : randomUUID();
    const directory = join(this.root, jobId);
    const reportPath = join(directory, 'report.json');
    if (existsSync(directory) || this.unpersisted.has(jobId)) {
      const inMemory = this.unpersisted.get(jobId);
      if (inMemory) {
        if (inMemory.fingerprint !== fingerprint)
          throw new AutomationFailure(
            'REQUEST_ID_CONFLICT',
            'Этот request_id уже связан с другими параметрами.',
          );
        return structuredClone(inMemory);
      }
      if (!existsSync(reportPath))
        throw new AutomationFailure(
          'BUSY',
          'Запрос регистрируется другим процессом; повторите тот же request_id.',
        );
      const previous = this.get(jobId);
      if (previous.fingerprint !== fingerprint)
        throw new AutomationFailure(
          'REQUEST_ID_CONFLICT',
          'Этот request_id уже связан с другими параметрами. Используйте исходные параметры или новый ID.',
        );
      return previous;
    }
    if (this.active)
      throw new AutomationFailure(
        'BUSY',
        `CorelDRAW занят заданием ${this.active.job_id}; сначала опросите corel_get_job.`,
      );
    try {
      mkdirSync(directory);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'EEXIST')
        throw new AutomationFailure('BUSY', 'Запрос уже регистрируется; повторите тот же request_id.');
      throw error;
    }
    const deadline =
      typeof args.deadline_seconds === 'number' ? args.deadline_seconds * 1000 : this.defaultDeadlineMs;
    const job: Job = {
      job_id: jobId,
      owner_pid: process.pid,
      owner_session: this.session,
      fingerprint,
      tool,
      status: 'running',
      stage: 'starting',
      started_at: new Date().toISOString(),
      deadline_at: new Date(Date.now() + deadline).toISOString(),
      deadline_exceeded: false,
      run_directory: directory,
      report_path: reportPath,
      log_path: join(directory, 'run.log'),
      artifacts: [],
      warnings: [],
      ...(requestId ? { request_id: requestId } : {}),
      versions: { server: VERSION, node: process.versions.node },
    };
    try {
      writeFileSync(join(directory, 'request.json'), JSON.stringify({ tool, args }, null, 2), 'utf8');
      this.log(job, `Отправлено: ${tool}`);
      this.save(job);
    } catch (error) {
      job.status = 'failed';
      job.stage = 'registering';
      job.finished_at = new Date().toISOString();
      job.error = {
        code: 'ARTIFACT_IO_ERROR',
        stage: job.stage,
        message: `COM-операция не запущена: ${error instanceof Error ? error.message : String(error)}`,
      };
      this.record(job, 'Регистрация завершилась до запуска операции.');
      return structuredClone(job);
    }
    this.active = job;
    const timer = setTimeout(() => {
      job.status = 'timed_out';
      job.deadline_exceeded = true;
      job.warnings.push('Срок истёк. COM-вызов ещё может выполняться; не повторяйте операцию.');
      this.record(job, 'Срок истёк; исполнитель остаётся занят.');
    }, deadline);
    void Promise.resolve()
      .then(() =>
        this.executor.run(job, args, (stage) => {
          job.stage = stage;
          this.record(job, stage);
        }),
      )
      .then((result) => {
        job.status = result.ok ? 'succeeded' : 'failed';
        job.stage = result.ok ? 'completed' : (result.error?.stage ?? job.stage);
        if (result.result !== undefined) job.result = result.result;
        if (result.error) job.error = result.error;
        if (result.cleanup) job.cleanup = result.cleanup;
        if (result.ok && result.cleanup?.completed === false)
          job.warnings.push(
            'Действие выполнено, но восстановление состояния не завершено. Автоматически повторять действие нельзя.',
          );
        if (result.compiler_location !== undefined) job.compiler_location = result.compiler_location;
        if (result.versions) job.versions = { ...(job.versions ?? {}), ...result.versions };
        job.artifacts = result.artifacts ?? [];
        job.warnings.push(...(result.warnings ?? []));
      })
      .catch((error) => {
        job.status = 'unknown';
        job.error = {
          code: 'EXECUTOR_LOST',
          stage: job.stage,
          message: `${error instanceof Error ? error.message : String(error)}. Проверьте CorelDRAW перед повтором с новым request_id.`,
        };
      })
      .finally(() => {
        clearTimeout(timer);
        job.finished_at = new Date().toISOString();
        this.record(job, `Завершено: ${job.status}`);
        this.active = undefined;
        if (this.closing) this.executor.close();
      });
    return structuredClone(job);
  }
  get(jobId: string): Job {
    if (!/^(?:[a-f0-9-]{36}|req-[a-f0-9]{64})$/.test(jobId))
      throw new AutomationFailure('INVALID_JOB_ID', 'Используйте job_id, возвращённый операцией.');
    if (this.active?.job_id === jobId) return structuredClone(this.active);
    const unpersisted = this.unpersisted.get(jobId);
    if (unpersisted) return structuredClone(unpersisted);
    const file = join(this.root, jobId, 'report.json');
    assertNoLinks(file);
    if (!existsSync(file)) throw new AutomationFailure('JOB_NOT_FOUND', `Неизвестное задание: ${jobId}`);
    const job = JSON.parse(readFileSync(file, 'utf8').replace(/^\uFEFF/, '')) as Job;
    if (['running', 'timed_out'].includes(job.status) && job.owner_session !== this.session) {
      try {
        process.kill(job.owner_pid, 0);
      } catch {
        job.status = 'unknown';
        job.error = {
          code: 'OWNER_LOST',
          stage: job.stage,
          message:
            'Владеющий сервер завершился. Исполнитель или CorelDRAW ещё могут работать; не повторяйте запрос.',
        };
      }
    }
    return job;
  }
  async wait(jobId: string, seconds: number): Promise<Job> {
    const until = Date.now() + seconds * 1000;
    let job = this.get(jobId);
    while (['running'].includes(job.status) && Date.now() < until) {
      await delay(Math.min(150, Math.max(1, until - Date.now())));
      job = this.get(jobId);
    }
    return job;
  }
  close(): void {
    this.closing = true;
    // Let an in-flight COM mutation finish and persist its report even after stdin EOF.
    if (!this.active) this.executor.close();
  }
}
