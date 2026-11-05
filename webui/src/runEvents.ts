export type RunEventResult<T = unknown> = { kind: 'apply'; run: T } | { kind: 'duplicate' | 'ignore' | 'recover'; run: T };

export function applyRunEvent<T extends { id: string; last_seq: number }>(run: T, event: { schema_version?: number; run_id?: string; seq?: number; state?: { status?: string } }): RunEventResult<T> {
  if (event.run_id !== run.id) return { kind: 'ignore', run };
  if (!Number.isSafeInteger(event.seq) || event.seq! < 1) return { kind: 'recover', run };
  if (event.seq! <= run.last_seq) return { kind: 'duplicate', run };
  if (event.schema_version !== 1 || event.seq !== run.last_seq + 1) return { kind: 'recover', run };
  return { kind: 'apply', run: { ...run, last_seq: event.seq!, ...(event.state?.status ? { status: event.state.status } : {}) } };
}
