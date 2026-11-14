import type { Arguments, Executor, Job, WorkerResult, WorkerReady } from './types.js';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { win32 } from 'node:path';
import * as z from 'zod';
import { VERSION } from './version.js';
import { encodeProtocolJson, JsonLineDecoder } from './worker-protocol.js';
import { postprocessWorkerResult } from './worker-results.js';

const jobId = z.string().regex(/^(?:[a-f0-9-]{36}|req-[a-f0-9]{64})$/);
const readySchema = z.strictObject({
  type: z.literal('ready'), protocol_version: z.literal(1), worker_version: z.literal(VERSION),
  runtime: z.string().regex(/^\.NET 10\./), architecture: z.literal('x64'),
});
const progressSchema = z.strictObject({ type: z.literal('progress'), job_id: jobId, stage: z.string().regex(/^[a-z0-9_]{1,100}$/) });
const resultSchema = z.strictObject({ type: z.literal('result'), job_id: jobId, data: z.strictObject({
  ok: z.boolean(), result: z.unknown().optional(),
  error: z.strictObject({ code: z.string().min(1).max(100), stage: z.string().min(1).max(100),
    message: z.string().max(16384), applied: z.boolean().optional(), number: z.number().int().optional(), source: z.string().max(4096).optional() }).optional(),
  cleanup: z.record(z.string(), z.unknown()).optional(), artifacts: z.array(z.string()).optional(),
  warnings: z.array(z.string()).optional(), versions: z.record(z.string(), z.string()).optional(),
}).refine(value => value.ok ? value.error === undefined : value.error !== undefined) });
const requestSchema = z.strictObject({ job_id: jobId, tool: z.string().regex(/^corel_[a-z_]{1,100}$/),
  args: z.record(z.string(), z.unknown()), run_directory: z.string().min(1).max(32767).refine(value => win32.isAbsolute(value) && !value.includes('\0')) });

export interface CSharpExecutorOptions {
  executionMode?: 'csharp' | 'hybrid';
  workerPath?: string;
  launchWorker?: () => ChildProcessWithoutNullStreams;
  readyTimeoutMs?: number;
}
interface Pending {
  job: Job; args: Arguments; frame: string; dispatched: boolean; warnings: string[];
  progress: (stage: string) => void; resolve: (value: WorkerResult) => void; reject: (error: Error) => void;
}

export class CSharpExecutor implements Executor {
  readonly backend = 'csharp' as const;
  private child: ChildProcessWithoutNullStreams | undefined;
  private pending: Pending | undefined;
  private ready = false;
  private identity: WorkerReady | undefined;
  private closing = false;
  private started = false;
  private unavailable: string | undefined;
  private timer: ReturnType<typeof setTimeout> | undefined;
  constructor(private options: CSharpExecutorOptions = {}) {}

  private failure(code: string, message: string): WorkerResult {
    return { ok: false, error: { code, message, stage: 'starting', applied: false } };
  }
  private warn(): void {
    const message = 'Предупреждение протокола C#-исполнителя: запись отклонена; результат операции ещё не подтверждён.';
    if (this.pending && this.pending.warnings.length < 5) this.pending.warnings.push(message);
    process.stderr.write(message + '\n');
  }
  private lost(message: string): void {
    this.unavailable ??= message;
    clearTimeout(this.timer);
    const pending = this.pending;
    this.pending = undefined;
    if (pending?.dispatched) pending.reject(new Error('Исход COM-операции неизвестен: результат исполнителя не получен. Автоматический повтор запрещён.'));
    else pending?.resolve(this.failure('WORKER_UNAVAILABLE', message));
    this.child?.stdin.end();
  }
  private protocolError(): void {
    if (this.pending?.dispatched) this.warn();
    else this.lost('Исполнитель не подтвердил совместимый JSONL-протокол; COM-задание не отправлено.');
  }
  private receive(value: unknown): void {
    if (this.unavailable) return;
    if (!this.ready) {
      const handshake = readySchema.safeParse(value);
      if (!handshake.success) { this.protocolError(); return; }
      this.identity = handshake.data;
      this.ready = true;
      clearTimeout(this.timer);
      this.dispatch();
      return;
    }
    const pending = this.pending;
    if (!pending?.dispatched) { this.protocolError(); return; }
    const progress = progressSchema.safeParse(value);
    if (progress.success && progress.data.job_id === pending.job.job_id) {
      try { pending.progress(progress.data.stage); } catch { this.warn(); }
      return;
    }
    const result = resultSchema.safeParse(value);
    if (!result.success || result.data.job_id !== pending.job.job_id) { this.warn(); return; }
    const data = postprocessWorkerResult(result.data.data as WorkerResult, pending.job, pending.args, pending.progress);
    data.versions = { ...(data.versions ?? {}), worker: this.identity!.worker_version, runtime: this.identity!.runtime,
      protocol: String(this.identity!.protocol_version), architecture: this.identity!.architecture };
    data.warnings = [...(data.warnings ?? []), ...pending.warnings];
    this.pending = undefined;
    pending.resolve(data);
    if (this.closing) this.child?.stdin.end();
  }
  private start(): void {
    if (this.started) return;
    this.started = true;
    try {
      if (!this.options.launchWorker && process.platform !== 'win32') throw new Error('Автоматизация CorelDRAW требует Windows.');
      const path = this.options.workerPath ?? fileURLToPath(new URL('../worker/publish/win-x64/CorelMcp.Worker.exe', import.meta.url));
      const child = this.options.launchWorker?.() ?? spawn(path, [], { windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'],
        env: { ...process.env, CORELDRAW_MCP_EXECUTION_MODE: this.options.executionMode ?? 'csharp' } });
      this.child = child;
      const reader = new JsonLineDecoder(value => this.receive(value), () => this.protocolError());
      child.stdout.on('data', (buffer: Buffer) => reader.feed(buffer));
      child.stdout.once('end', () => { reader.end(); this.lost('EOF исполнителя: полный результат не получен.'); });
      child.stdout.once('error', () => this.lost('Потерян канал чтения исполнителя.'));
      child.stdin.on('error', () => this.lost('Потерян канал отправки исполнителю.'));
      let stderrBytes = 0;
      child.stderr.on('data', (buffer: Buffer) => {
        if (stderrBytes < 65536) process.stderr.write(buffer.subarray(0, 65536 - stderrBytes));
        stderrBytes += buffer.byteLength;
      });
      child.once('error', () => this.lost('Не удалось запустить C#-исполнитель; задание не было отправлено.'));
      // 'close' follows stdout drain. 'exit' can precede a valid final frame still in the pipe.
      child.once('close', () => this.lost('C#-исполнитель завершился; результат не получен.'));
      this.timer = setTimeout(() => {
        if (!this.ready) this.lost('Истёк срок ожидания ready; COM-задание не отправлено.');
      }, this.options.readyTimeoutMs ?? 10000);
    } catch { this.lost('Не удалось запустить C#-исполнитель; COM-задание не отправлено.'); }
  }
  private dispatch(): void {
    const pending = this.pending;
    if (!this.ready || !pending || pending.dispatched || this.unavailable) return;
    // Once write begins, an error cannot prove the request was not delivered.
    pending.dispatched = true;
    try { this.child!.stdin.write(pending.frame + '\n', 'utf8', error => { if (error) this.lost('Ошибка отправки задания исполнителю.'); }); }
    catch { this.lost('Ошибка отправки задания исполнителю.'); }
  }
  run(job: Job, args: Arguments, progress: (stage: string) => void): Promise<WorkerResult> {
    if (this.closing) return Promise.resolve(this.failure('SHUTTING_DOWN', 'Исполнитель завершает работу.'));
    if (this.pending) return Promise.resolve(this.failure('BUSY', 'Исполнитель уже занят; дождитесь результата текущего задания.'));
    if (this.unavailable) return Promise.resolve(this.failure('WORKER_UNAVAILABLE', this.unavailable));
    let frame: string;
    try {
      const request = { job_id: job.job_id, tool: job.tool, args, run_directory: job.run_directory };
      if (!requestSchema.safeParse(request).success) throw new Error('Некорректный запрос.');
      frame = encodeProtocolJson(request);
    } catch { return Promise.resolve(this.failure('WORKER_INVALID_REQUEST', 'Запрос не соответствует ограниченному JSONL-контракту; COM-задание не отправлено.')); }
    return new Promise((resolve, reject) => {
      this.pending = { job, args, frame, dispatched: false, warnings: [], progress, resolve, reject };
      this.start();
      this.dispatch();
    });
  }
  close(): void {
    this.closing = true;
    if (!this.pending) this.child?.stdin.end();
  }
}
