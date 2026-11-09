import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join, resolve } from 'node:path';
import { assertNoLinks } from './artifacts.js';
import type { Job, VbaObserverSummary } from './types.js';

export type ObserverMode = 'off' | 'observe';
export type ObserverCode = 'VBA_DIALOG_DETECTED' | 'VBA_DIALOG_DISMISSED' | 'VBA_DIALOG_REQUIRES_OPERATOR'
  | 'VBA_OBSERVER_UNAVAILABLE' | 'VBA_OBSERVER_PROTOCOL_ERROR' | 'VBA_OBSERVER_LEASE_LOST' | 'VBA_DIALOG_ACTION_UNCERTAIN';
const codes = new Set<ObserverCode>(['VBA_DIALOG_DETECTED', 'VBA_DIALOG_DISMISSED', 'VBA_DIALOG_REQUIRES_OPERATOR',
  'VBA_OBSERVER_UNAVAILABLE', 'VBA_OBSERVER_PROTOCOL_ERROR', 'VBA_OBSERVER_LEASE_LOST', 'VBA_DIALOG_ACTION_UNCERTAIN']);
export function observerMode(value: string | undefined): ObserverMode {
  if (value === undefined || value === 'observe') return 'observe';
  if (value === 'off') return 'off';
  if (value === 'compile-errors') throw new Error('Режим compile-errors пока не поддерживается: профиль диалогов не подтверждён.');
  throw new Error('Недопустимый CORELDRAW_MCP_VBA_OBSERVER: ожидается off или observe.');
}
export function emptyObserverSummary(mode: ObserverMode): VbaObserverSummary {
  return { schema_version: 1, mode, state: mode === 'off' ? 'stopped' : 'starting', detected: 0,
    action_attempted: 0, dismissed: 0, requires_operator: 0, events_truncated: false };
}
export function safeObserverSummary(value: unknown, directory?: string): VbaObserverSummary | undefined {
  if (!value || typeof value !== 'object') return;
  const v = value as Record<string, unknown>;
  if (v.schema_version !== 1 || typeof v.mode !== 'string' || !['off', 'observe', 'compile-errors'].includes(v.mode) ||
    typeof v.state !== 'string' || !['starting', 'observing', 'degraded', 'stopped'].includes(v.state) || typeof v.events_truncated !== 'boolean') return;
  for (const key of ['detected', 'action_attempted', 'dismissed', 'requires_operator'])
    if (!Number.isSafeInteger(v[key]) || (v[key] as number) < 0) return;
  if ((v.dismissed as number) > (v.action_attempted as number) || (v.action_attempted as number) > (v.detected as number) ||
    (v.requires_operator as number) > (v.detected as number) ||
    (v.mode !== 'compile-errors' && (v.action_attempted !== 0 || v.dismissed !== 0))) return;
  if (v.last_code !== undefined && !codes.has(v.last_code as ObserverCode)) return;
  const summary: VbaObserverSummary = {
    schema_version: 1, mode: v.mode as VbaObserverSummary['mode'], state: v.state as VbaObserverSummary['state'],
    detected: v.detected as number, action_attempted: v.action_attempted as number, dismissed: v.dismissed as number,
    requires_operator: v.requires_operator as number, events_truncated: v.events_truncated,
    ...(v.last_code === undefined ? {} : { last_code: v.last_code as ObserverCode }),
  };
  if (v.artifact !== undefined) {
    if (typeof v.artifact !== 'string' || !directory || resolve(v.artifact) !== resolve(join(directory, 'vba-observer.jsonl'))) return;
    try { assertNoLinks(v.artifact); } catch { return; }
    summary.artifact = v.artifact;
  }
  return summary;
}
export function mergeObserverSummary(previous: VbaObserverSummary | undefined, next: VbaObserverSummary | undefined): VbaObserverSummary | undefined {
  if (!next) return previous;
  if (!previous) return next;
  if (previous.mode !== next.mode) return previous;
  return { ...previous, ...next, detected: Math.max(previous.detected, next.detected),
    action_attempted: Math.max(previous.action_attempted, next.action_attempted), dismissed: Math.max(previous.dismissed, next.dismissed),
    requires_operator: Math.max(previous.requires_operator, next.requires_operator), events_truncated: previous.events_truncated || next.events_truncated };
}
export function publicObserverSummary(value: unknown): VbaObserverSummary | undefined {
  if (!value || typeof value !== 'object') return;
  const { artifact: _artifact, ...data } = value as Record<string, unknown>;
  return safeObserverSummary(data);
}

interface ProcessIdentity { pid: number; start_time: string }
interface AppIdentity extends ProcessIdentity { hwnd: string; tid: number; desktop: string; session: number }
export interface ObserverLease {
  schema_version: 1; job_id: string; generation: number; token: string;
  owner: ProcessIdentity; worker: ProcessIdentity; app_hwnd: string;
  phase: string; project_name?: string;
}
export type ObserverCommand = (ObserverLease & { type: 'arm' }) | {
  schema_version: 1; job_id: string; generation: number; token: string; type: 'renew' | 'disarm' | 'stop';
};
export type ObserverEvent = { schema_version: 1; sequence: number } & (
  { type: 'ready'; owner: ProcessIdentity; worker: ProcessIdentity } |
  ({ job_id: string; generation: number; token: string } & (
    { type: 'armed'; identity: AppIdentity } |
    { type: 'observation'; candidate_id: string; code: 'VBA_DIALOG_REQUIRES_OPERATOR'; artifact?: string; events_truncated?: boolean } |
    { type: 'degraded'; code: ObserverCode; events_truncated?: boolean } |
    { type: 'disarmed' | 'stopped' }
  ))
);
export interface ObserverPhaseResult { available: boolean; compile_error_confirmed: false }
interface ObserverOptions { launch?: () => ChildProcessWithoutNullStreams; startupMs?: number; handshakeMs?: number }
interface Waiter { type: string; generation: number; resolve: (ok: boolean) => void; timer: ReturnType<typeof setTimeout> }
function processIdentity(value: unknown, pid: number): value is ProcessIdentity {
  if (!value || typeof value !== 'object') return false;
  const v = value as Record<string, unknown>;
  return v.pid === pid && typeof v.start_time === 'string' && /^\d{1,20}$/.test(v.start_time);
}
function appIdentity(value: unknown, hwnd: string): value is AppIdentity {
  if (!value || typeof value !== 'object') return false;
  const v = value as AppIdentity;
  return processIdentity(v, v.pid) && Number.isSafeInteger(v.pid) && v.pid > 0 && v.hwnd === hwnd &&
    Number.isSafeInteger(v.tid) && v.tid > 0 && Number.isSafeInteger(v.session) && v.session >= 0 &&
    typeof v.desktop === 'string' && /^[^\x00-\x1f]{1,256}$/.test(v.desktop);
}

export class VbaObserver {
  summary = emptyObserverSummary('observe');
  private child: ChildProcessWithoutNullStreams | undefined;
  private owner: ProcessIdentity | undefined;
  private worker: ProcessIdentity | undefined;
  private identity: AppIdentity | undefined;
  private lease: ObserverLease | undefined;
  private waiter: Waiter | undefined;
  private generation = 0;
  private sequence = 0;
  private buffer = Buffer.alloc(0);
  private candidates = new Set<string>();
  private renewal: ReturnType<typeof setInterval> | undefined;
  private killTimer: ReturnType<typeof setTimeout> | undefined;
  private received = 0;
  private publishTimer: ReturnType<typeof setTimeout> | undefined;
  private exited = false;
  private failed = false;
  private stopping = false;
  private stopAck = false;
  private active = false;
  private startPromise: Promise<boolean> | undefined;
  private stopPromise: Promise<void> | undefined;
  constructor(private job: Pick<Job, 'job_id' | 'run_directory'>, private workerPid: number,
    private update: (summary: VbaObserverSummary) => void, private options: ObserverOptions = {}) {}
  private publish(throttle = false): void {
    if (throttle) {
      this.publishTimer ??= setTimeout(() => { this.publishTimer = undefined; this.publish(); }, 250);
      return;
    }
    if (this.publishTimer) { clearTimeout(this.publishTimer); this.publishTimer = undefined; }
    try { this.update(structuredClone(this.summary)); } catch { /* Never reject a COM job from diagnostics. */ }
  }
  private complete(ok: boolean): void {
    if (!this.waiter) return;
    const waiter = this.waiter; this.waiter = undefined; clearTimeout(waiter.timer); waiter.resolve(ok);
  }
  private wait(type: string, generation: number, ms: number): Promise<boolean> {
    this.complete(false);
    return new Promise(resolve => {
      this.waiter = { type, generation, resolve, timer: setTimeout(() => { this.complete(false); this.degrade('VBA_OBSERVER_UNAVAILABLE'); }, ms) };
    });
  }
  private send(value: object): boolean {
    if (!this.child || this.exited || this.child.stdin.destroyed || this.child.stdin.writableLength > 65536) return false;
    try { this.child.stdin.write(JSON.stringify(value) + '\n', error => { if (error) this.degrade('VBA_OBSERVER_UNAVAILABLE'); }); return true; }
    catch { this.degrade('VBA_OBSERVER_UNAVAILABLE'); return false; }
  }
  private command(type: 'disarm' | 'renew' | 'stop'): object {
    return { schema_version: 1, type, job_id: this.job.job_id, generation: this.lease?.generation ?? 0, token: this.lease?.token ?? '' };
  }
  private degrade(code: ObserverCode): void {
    if (this.stopping || this.failed) return;
    this.failed = true; this.active = false;
    if (this.renewal) clearInterval(this.renewal);
    this.summary.state = 'degraded'; this.summary.last_code = code;
    this.complete(false); this.publish();
    // Observe-only: terminate only this private diagnostic child, never the COM worker.
    if (this.child && !this.exited) {
      this.send(this.command('stop')); this.child.stdin.end();
      this.killTimer = setTimeout(() => { if (!this.exited) this.child?.kill(); }, this.options.handshakeMs ?? 2000);
    }
  }
  start(): Promise<boolean> {
    if (this.startPromise) return this.startPromise;
    this.startPromise = this.startInternal(); return this.startPromise;
  }
  private async startInternal(): Promise<boolean> {
    this.publish();
    const ready = this.wait('ready', 0, this.options.startupMs ?? 10000);
    try {
      const script = fileURLToPath(new URL('../powershell/vba-observer.ps1', import.meta.url));
      this.child = this.options.launch?.() ?? spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', script, '-OwnerPid', String(process.pid), '-WorkerPid', String(this.workerPid)],
      { shell: false, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
      this.child.stdin.on('error', () => this.degrade('VBA_OBSERVER_UNAVAILABLE'));
      this.child.stdout.on('data', (chunk: Buffer) => this.consume(chunk));
      this.child.stdout.once('end', () => { if (!this.stopping) this.degrade(this.buffer.length ? 'VBA_OBSERVER_PROTOCOL_ERROR' : 'VBA_OBSERVER_UNAVAILABLE'); });
      let stderrBytes = 0;
      this.child.stderr.on('data', (chunk: Buffer) => { stderrBytes += chunk.length; if (stderrBytes > 65536) this.degrade('VBA_OBSERVER_PROTOCOL_ERROR'); });
      this.child.once('error', () => { this.exited = true; this.degrade('VBA_OBSERVER_UNAVAILABLE'); });
      this.child.once('exit', () => {
        this.exited = true; if (this.killTimer) clearTimeout(this.killTimer);
        this.complete(false); if (!this.stopping) this.degrade('VBA_OBSERVER_UNAVAILABLE');
      });
    } catch { this.degrade('VBA_OBSERVER_UNAVAILABLE'); }
    return ready;
  }
  private consume(chunk: Buffer): void {
    if (this.failed && !this.stopping) return;
    let offset = 0;
    while (offset < chunk.length) {
      const newline = chunk.indexOf(10, offset);
      const end = newline < 0 ? chunk.length : newline;
      if (this.buffer.length + end - offset > 65536) { this.buffer = Buffer.alloc(0); this.degrade('VBA_OBSERVER_PROTOCOL_ERROR'); return; }
      this.buffer = Buffer.concat([this.buffer, chunk.subarray(offset, end)]);
      if (newline < 0) return;
      const bytes = this.buffer; this.buffer = Buffer.alloc(0); offset = newline + 1;
      try { this.event(JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes).replace(/^\uFEFF/, ''))); }
      catch { this.degrade('VBA_OBSERVER_PROTOCOL_ERROR'); return; }
    }
  }
  private event(value: unknown): void {
    if (++this.received > 1000) { this.degrade('VBA_OBSERVER_PROTOCOL_ERROR'); return; }
    if (!value || typeof value !== 'object') { this.degrade('VBA_OBSERVER_PROTOCOL_ERROR'); return; }
    const v = value as Record<string, any>;
    if (v.schema_version !== 1 || !Number.isSafeInteger(v.sequence) || v.sequence <= 0) { this.degrade('VBA_OBSERVER_PROTOCOL_ERROR'); return; }
    if (this.stopping) {
      if (v.type === 'stopped' && (!this.lease ||
        (v.job_id === this.lease.job_id && v.generation === this.lease.generation && v.token === this.lease.token))) this.stopAck = true;
      return;
    }
    if (v.type === 'ready') {
      if (this.waiter?.type !== 'ready' || !processIdentity(v.owner, process.pid) || !processIdentity(v.worker, this.workerPid)) {
        this.degrade('VBA_OBSERVER_PROTOCOL_ERROR'); return;
      }
      this.owner = v.owner; this.worker = v.worker; this.sequence = v.sequence; this.complete(true); return;
    }
    if (!this.lease || v.job_id !== this.lease.job_id || v.generation !== this.lease.generation || v.token !== this.lease.token) return;
    if (v.sequence <= this.sequence) return;
    this.sequence = v.sequence;
    if (v.type === 'armed') {
      if (this.waiter?.type !== 'armed') { this.send(this.command('disarm')); return; }
      if (!appIdentity(v.identity, this.lease.app_hwnd) || (this.identity &&
        (['pid', 'tid', 'hwnd', 'start_time', 'desktop', 'session'] as const).some(key => v.identity[key] !== this.identity![key]))) {
        this.degrade('VBA_OBSERVER_LEASE_LOST'); return;
      }
      this.identity = v.identity; this.active = true; this.summary.state = 'observing'; this.complete(true); this.publish();
    } else if (v.type === 'disarmed') {
      this.active = false;
      if (this.waiter?.type === 'disarmed') this.complete(true);
    } else if (v.type === 'observation' && this.active) {
      if (v.code !== 'VBA_DIALOG_REQUIRES_OPERATOR' || typeof v.candidate_id !== 'string' || !/^[A-Za-z0-9:_-]{1,128}$/.test(v.candidate_id)) {
        this.degrade('VBA_OBSERVER_PROTOCOL_ERROR'); return;
      }
      if (this.candidates.has(v.candidate_id)) return;
      if (this.candidates.size >= 100) { this.summary.events_truncated = true; this.degrade('VBA_OBSERVER_PROTOCOL_ERROR'); return; }
      this.candidates.add(v.candidate_id); this.summary.detected++; this.summary.requires_operator++;
      this.summary.last_code = 'VBA_DIALOG_REQUIRES_OPERATOR';
      if (v.artifact !== undefined) {
        const checked = safeObserverSummary({ ...this.summary, artifact: v.artifact }, this.job.run_directory);
        if (!checked) { this.degrade('VBA_OBSERVER_PROTOCOL_ERROR'); return; }
        this.summary = checked;
      }
      if (v.events_truncated === true) this.summary.events_truncated = true;
      this.publish(true);
    } else if (v.type === 'degraded') {
      if (v.events_truncated === true) this.summary.events_truncated = true;
      this.degrade(codes.has(v.code) ? v.code : 'VBA_OBSERVER_PROTOCOL_ERROR');
    } else { this.degrade('VBA_OBSERVER_PROTOCOL_ERROR'); }
  }
  async arm(phase: string, project: string | undefined, app: { hwnd: string } | string): Promise<ObserverPhaseResult> {
    const unavailable: ObserverPhaseResult = { available: false, compile_error_confirmed: false };
    if (!await this.start() || this.failed || this.stopping || !this.owner || !this.worker) return unavailable;
    await this.disarm();
    if (this.failed) return unavailable;
    const hwnd = typeof app === 'string' ? app : app.hwnd;
    if (!/^[1-9]\d{0,19}$/.test(hwnd) || !/^[a-z_]{1,32}$/.test(phase) ||
      (project !== undefined && !/^CorelMcp_[A-Za-z0-9_]{1,64}$/.test(project))) {
      this.degrade('VBA_OBSERVER_UNAVAILABLE'); return unavailable;
    }
    this.lease = { schema_version: 1, job_id: this.job.job_id, generation: ++this.generation, token: randomBytes(24).toString('hex'),
      owner: this.owner, worker: this.worker, app_hwnd: hwnd, phase, ...(project === undefined ? {} : { project_name: project }) };
    const waiting = this.wait('armed', this.generation, this.options.handshakeMs ?? 750);
    if (!this.send({ ...this.lease, type: 'arm', run_directory: this.job.run_directory })) this.degrade('VBA_OBSERVER_UNAVAILABLE');
    const armed = await waiting;
    if (armed) this.renewal = setInterval(() => { if (this.active) this.send(this.command('renew')); }, 500);
    return { available: armed, compile_error_confirmed: false };
  }
  async disarm(): Promise<ObserverPhaseResult> {
    if (this.renewal) clearInterval(this.renewal);
    const available = this.active && !this.failed;
    if (!available || !this.lease) return { available: false, compile_error_confirmed: false };
    this.active = false;
    const waiting = this.wait('disarmed', this.lease.generation, this.options.handshakeMs ?? 750);
    if (!this.send(this.command('disarm'))) this.degrade('VBA_OBSERVER_UNAVAILABLE');
    return { available: await waiting, compile_error_confirmed: false };
  }
  stop(): Promise<void> { return this.stopPromise ??= this.stopInternal(); }
  private async stopInternal(): Promise<void> {
    if (this.renewal) clearInterval(this.renewal);
    this.active = false; this.stopping = true; this.complete(false);
    if (this.child && !this.exited) {
      const child = this.child;
      await new Promise<void>(resolve => {
        const failed = () => {
          this.summary.state = 'degraded';
          if (!this.failed) this.summary.last_code = 'VBA_OBSERVER_UNAVAILABLE';
          this.publish();
        };
        const timer = setTimeout(() => { failed(); child.kill(); }, this.options.handshakeMs ?? 2000);
        // close follows process exit AND drainage of its stdout/stderr.
        child.once('close', (code) => {
          clearTimeout(timer); if (code !== 0 || !this.stopAck) failed(); resolve();
        });
        this.send(this.command('stop')); child.stdin.end();
      });
    }
    this.summary.state = 'stopped'; this.publish();
  }
}
