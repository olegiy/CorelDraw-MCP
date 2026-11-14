export type Arguments = Record<string, unknown>;
export interface WorkerReady {
  type: 'ready';
  protocol_version: 1;
  worker_version: string;
  runtime: string;
  architecture: 'x64';
}
export interface VbaObserverSummary {
  schema_version: 1;
  mode: 'off' | 'observe' | 'compile-errors';
  state: 'starting' | 'observing' | 'degraded' | 'stopped';
  detected: number;
  action_attempted: number;
  dismissed: number;
  requires_operator: number;
  last_code?: import('./historical-reports.js').ObserverCode;
  events_truncated: boolean;
  artifact?: string;
}
export type JobStatus = 'running' | 'timed_out' | 'succeeded' | 'failed' | 'unknown';
export interface AutomationError {
  code: string;
  message: string;
  stage: string;
  number?: number;
  source?: string;
  applied?: boolean;
}
export interface WorkerResult {
  vba_observer?: VbaObserverSummary;
  ok: boolean;
  result?: unknown;
  error?: AutomationError;
  cleanup?: Record<string, unknown>;
  compiler_location?: Record<string, unknown> | null;
  artifacts?: string[];
  warnings?: string[];
  versions?: Record<string, string>;
}
export interface Job {
  vba_observer?: VbaObserverSummary;
  job_id: string;
  owner_pid: number;
  owner_session: string;
  request_id?: string;
  fingerprint: string;
  tool: string;
  status: JobStatus;
  stage: string;
  started_at: string;
  finished_at?: string;
  deadline_at: string;
  deadline_exceeded: boolean;
  run_directory: string;
  report_path: string;
  log_path: string;
  result?: unknown;
  error?: AutomationError;
  cleanup?: Record<string, unknown>;
  compiler_location?: Record<string, unknown> | null;
  artifacts: string[];
  warnings: string[];
  versions?: Record<string, string>;
  details_pruned?: boolean;
  pruned_inventory?: { token: string; bytes: number };
  // Historical report metadata only; new jobs never generate permissions.
  tool_policy?: Record<string, unknown>;
}
export interface Executor {
  readonly backend?: 'csharp';
  run(job: Job, args: Arguments, progress: (stage: string) => void): Promise<WorkerResult>;
  close(): void;
}
export class AutomationFailure extends Error {
  constructor(
    public code: string,
    message: string,
  ) {
    super(message);
  }
}
