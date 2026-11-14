// Historical report DTO readers only. No observer process or native actions.
import { join, resolve } from 'node:path';
import { assertNoLinks } from './artifacts.js';
import type { VbaObserverSummary } from './types.js';

export type ObserverCode = 'VBA_DIALOG_DETECTED' | 'VBA_DIALOG_DISMISSED' | 'VBA_DIALOG_REQUIRES_OPERATOR'
  | 'VBA_OBSERVER_UNAVAILABLE' | 'VBA_OBSERVER_PROTOCOL_ERROR' | 'VBA_OBSERVER_LEASE_LOST' | 'VBA_DIALOG_ACTION_UNCERTAIN';
const codes = new Set<ObserverCode>(['VBA_DIALOG_DETECTED', 'VBA_DIALOG_DISMISSED', 'VBA_DIALOG_REQUIRES_OPERATOR',
  'VBA_OBSERVER_UNAVAILABLE', 'VBA_OBSERVER_PROTOCOL_ERROR', 'VBA_OBSERVER_LEASE_LOST', 'VBA_DIALOG_ACTION_UNCERTAIN']);
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
