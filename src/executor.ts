import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { type Arguments, type Executor, type Job, type WorkerResult } from './types.js';

export class PowerShellExecutor implements Executor {
  private child: ChildProcessWithoutNullStreams | undefined;
  private unavailable: Error | undefined;
  private pending: { id: string; resolve: (value: WorkerResult) => void; reject: (error: Error) => void; progress: (stage: string) => void; warnings: string[] } | undefined;
  constructor(private progId = 'CorelDRAW.Application.27', private launchWorker?: () => ChildProcessWithoutNullStreams) {}
  private lost(error: Error): void {
    this.unavailable = error;
    this.pending?.reject(error);
    this.pending = undefined;
  }
  private warn(message: string): void {
    // Unrecognized output does not prove a COM call has finished. Retain the
    // pending slot until a matching result or process exit is observed.
    const warning = `Executor protocol warning: ${message}`;
    if (this.pending && this.pending.warnings.length < 5) this.pending.warnings.push(warning);
    process.stderr.write(`${warning}\n`);
  }
  private start(): void {
    if (this.child) {
      if (this.unavailable) throw this.unavailable;
      return;
    }
    if (process.platform !== 'win32') throw new Error('CorelDRAW COM automation requires Windows.');
    const script = fileURLToPath(new URL('../powershell/worker.ps1', import.meta.url));
    const child = this.launchWorker?.() ?? spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-STA', '-ExecutionPolicy', 'Bypass', '-File', script, '-ProgId', this.progId], {
      windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'],
    });
    this.child = child;
    this.unavailable = undefined;
    child.stderr.on('data', buffer => process.stderr.write(buffer));
    const lines = createInterface({ input: child.stdout });
    lines.on('line', line => {
      let message;
      try { message = JSON.parse(line.replace(/^\uFEFF/, '')); }
      catch { this.warn('Ignored malformed JSON output; waiting for the running operation.'); return; }
      if (!message || typeof message !== 'object') { this.warn('Ignored a non-message output value.'); return; }
      if (message.job_id !== this.pending?.id || !this.pending) return;
      try {
        if (message.type === 'validate_json') {
          let valid = false;
          if (typeof message.json === 'string' && Buffer.byteLength(message.json, 'utf8') <= 1048576) {
            try { JSON.parse(message.json); valid = true; } catch { /* Reply with a validation failure, preserving the GMS. */ }
          }
          child.stdin.write(JSON.stringify({ type: 'json_validation', job_id: this.pending.id, ok: valid }) + '\n', 'utf8', error => { if (error) this.lost(error); });
        }
        else if (message.type === 'progress' && typeof message.stage === 'string') this.pending.progress(message.stage);
        else if (message.type === 'result' && typeof message.data?.ok === 'boolean') {
          const result = message.data as WorkerResult;
          if (result.result && typeof result.result === 'object' && 'value_json' in result.result) {
            const { value_json, ...details } = result.result as Record<string, unknown>;
            if (typeof value_json !== 'string') throw new Error('VBA result is not a JSON string.');
            result.result = { ...details, value: JSON.parse(value_json) };
          }
          result.warnings = [...(result.warnings ?? []), ...this.pending.warnings];
          const pending = this.pending; this.pending = undefined; pending.resolve(result);
        }
        else this.warn('Ignored an unrecognized message; waiting for a complete result.');
      } catch (error) {
        this.warn(error instanceof Error ? error.message : String(error));
      }
    });
    child.once('error', error => {
      if (this.child !== child) return;
      this.lost(error);
      if (!child.pid) this.child = undefined;
    });
    child.once('exit', code => {
      lines.close();
      if (this.child !== child) return;
      this.lost(new Error(`PowerShell executor exited (${code}). Document identifiers are invalid after reconnection`));
      this.child = undefined; this.unavailable = undefined;
    });
  }
  run(job: Job, args: Arguments, progress: (stage: string) => void): Promise<WorkerResult> {
    this.start();
    if (this.pending) return Promise.reject(new Error('Executor is already busy.'));
    return new Promise((resolve, reject) => {
      this.pending = { id: job.job_id, resolve, reject, progress, warnings: [] };
      this.child!.stdin.write(JSON.stringify({ job_id: job.job_id, tool: job.tool, args, run_directory: job.run_directory }) + '\n', 'utf8', error => {
        if (error) this.lost(error);
      });
    });
  }
  close(): void { this.child?.stdin.end(); }
}
