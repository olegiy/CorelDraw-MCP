import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { type Arguments, type Executor, type Job, type WorkerResult } from './types.js';
import { AutomationFailure } from './types.js';
import { lstatSync, readFileSync } from 'node:fs';
import { isAbsolute, relative } from 'node:path';
import { assertNoLinks } from './artifacts.js';
import { measureSnapshot } from './geometry.js';

export class PowerShellExecutor implements Executor {
  private child: ChildProcessWithoutNullStreams | undefined;
  private unavailable: Error | undefined;
  private pending:
    | {
        id: string;
        job: Job;
        args: Arguments;
        resolve: (value: WorkerResult) => void;
        reject: (error: Error) => void;
        progress: (stage: string) => void;
        warnings: string[];
      }
    | undefined;
  constructor(
    private progId = 'CorelDRAW.Application.27',
    private launchWorker?: () => ChildProcessWithoutNullStreams,
  ) {}
  private lost(error: Error): void {
    this.unavailable = error;
    this.pending?.reject(error);
    this.pending = undefined;
  }
  private warn(message: string): void {
    // Unrecognized output does not prove a COM call has finished. Retain the
    // pending slot until a matching result or process exit is observed.
    const warning = `Предупреждение протокола исполнителя: ${message}`;
    if (this.pending && this.pending.warnings.length < 5) this.pending.warnings.push(warning);
    process.stderr.write(`${warning}\n`);
  }
  private start(): void {
    if (this.child) {
      if (this.unavailable) throw this.unavailable;
      return;
    }
    if (process.platform !== 'win32') throw new Error('Автоматизация CorelDRAW через COM требует Windows.');
    const script = fileURLToPath(new URL('../powershell/worker.ps1', import.meta.url));
    const child =
      this.launchWorker?.() ??
      spawn(
        'powershell.exe',
        [
          '-NoLogo',
          '-NoProfile',
          '-NonInteractive',
          '-STA',
          '-ExecutionPolicy',
          'Bypass',
          '-File',
          script,
          '-ProgId',
          this.progId,
        ],
        {
          windowsHide: true,
          shell: false,
          stdio: ['pipe', 'pipe', 'pipe'],
        },
      );
    this.child = child;
    this.unavailable = undefined;
    child.stderr.on('data', (buffer) => process.stderr.write(buffer));
    const lines = createInterface({ input: child.stdout });
    lines.on('line', (line) => {
      let message;
      try {
        message = JSON.parse(line.replace(/^\uFEFF/, ''));
      } catch {
        this.warn('Некорректный JSON пропущен; ожидается результат текущей операции.');
        return;
      }
      if (!message || typeof message !== 'object') {
        this.warn('Значение вне протокола пропущено.');
        return;
      }
      if (message.job_id !== this.pending?.id || !this.pending) return;
      try {
        if (message.type === 'validate_json') {
          let valid = false;
          if (typeof message.json === 'string' && Buffer.byteLength(message.json, 'utf8') <= 1048576) {
            try {
              JSON.parse(message.json);
              valid = true;
            } catch {
              /* Reply with a validation failure, preserving the GMS. */
            }
          }
          child.stdin.write(
            JSON.stringify({ type: 'json_validation', job_id: this.pending.id, ok: valid }) + '\n',
            'utf8',
            (error) => {
              if (error) this.lost(error);
            },
          );
        } else if (message.type === 'progress' && typeof message.stage === 'string')
          this.pending.progress(message.stage);
        else if (message.type === 'result' && typeof message.data?.ok === 'boolean') {
          const result = message.data as WorkerResult;
          if (result.result && typeof result.result === 'object' && 'value_json' in result.result) {
            const { value_json, ...details } = result.result as Record<string, unknown>;
            if (typeof value_json !== 'string') throw new Error('Результат VBA не является строкой JSON.');
            result.result = { ...details, value: JSON.parse(value_json) };
          }
          if (result.ok && this.pending.job.tool === 'corel_measure_shapes') {
            try {
              const snapshotPath = (result.result as { snapshot_path?: unknown })?.snapshot_path;
              if (typeof snapshotPath !== 'string')
                throw new AutomationFailure(
                  'GEOMETRY_INVALID_INPUT',
                  'Исполнитель не вернул файл геометрии.',
                );
              const rel = relative(this.pending.job.run_directory, snapshotPath);
              if (
                isAbsolute(rel) ||
                rel === '..' ||
                rel.startsWith('..\\') ||
                rel.startsWith('../') ||
                !result.artifacts?.includes(snapshotPath)
              )
                throw new AutomationFailure('GEOMETRY_INVALID_PATH', 'Файл геометрии вне каталога задания.');
              assertNoLinks(snapshotPath);
              const info = lstatSync(snapshotPath);
              if (!info.isFile() || info.size > 64 * 1024 * 1024)
                throw new AutomationFailure('GEOMETRY_LIMIT_EXCEEDED', 'Файл геометрии превышает 64 МиБ.');
              this.pending.progress('calculating_geometry');
              const details = result.result as Record<string, unknown>;
              result.result = {
                ...measureSnapshot(
                  JSON.parse(readFileSync(snapshotPath, 'utf8').replace(/^\uFEFF/, '')),
                  Number(this.pending.args.tolerance_mm ?? 0.01),
                ),
                original_state_preserved: details.original_state_preserved === true,
              };
            } catch (error) {
              result.ok = false;
              result.error = {
                code: error instanceof AutomationFailure ? error.code : 'GEOMETRY_INVALID_INPUT',
                stage: 'calculating_geometry',
                message: error instanceof Error ? error.message : String(error),
              };
            }
          }
          result.warnings = [...(result.warnings ?? []), ...this.pending.warnings];
          const pending = this.pending;
          this.pending = undefined;
          pending.resolve(result);
        } else this.warn('Неизвестное сообщение пропущено; ожидается полный результат.');
      } catch (error) {
        this.warn(error instanceof Error ? error.message : String(error));
      }
    });
    child.once('error', (error) => {
      if (this.child !== child) return;
      this.lost(error);
      if (!child.pid) this.child = undefined;
    });
    child.once('exit', (code) => {
      lines.close();
      if (this.child !== child) return;
      this.lost(
        new Error(
          `Исполнитель PowerShell завершился (${code}). После переподключения ID документов недействительны.`,
        ),
      );
      this.child = undefined;
      this.unavailable = undefined;
    });
  }
  run(job: Job, args: Arguments, progress: (stage: string) => void): Promise<WorkerResult> {
    this.start();
    if (this.pending) return Promise.reject(new Error('Исполнитель уже занят.'));
    return new Promise((resolve, reject) => {
      this.pending = { id: job.job_id, job, args, resolve, reject, progress, warnings: [] };
      this.child!.stdin.write(
        JSON.stringify({ job_id: job.job_id, tool: job.tool, args, run_directory: job.run_directory }) + '\n',
        'utf8',
        (error) => {
          if (error) this.lost(error);
        },
      );
    });
  }
  close(): void {
    this.child?.stdin.end();
  }
}
