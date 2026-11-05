import { expect, test } from '@playwright/test';

test('event reducer rejects gaps and unknown schema and ignores duplicates', async () => {
  const { applyRunEvent } = await import('../src/runEvents');
  const run = { id: 'fixture', status: 'running', last_seq: 5 } as never;
  expect(applyRunEvent(run, { schema_version: 1, run_id: 'fixture', seq: 5 })).toEqual({ kind: 'duplicate', run });
  expect(applyRunEvent(run, { schema_version: 99, run_id: 'fixture', seq: 6 }).kind).toBe('recover');
  expect(applyRunEvent(run, { schema_version: 1, run_id: 'fixture', seq: 7 }).kind).toBe('recover');
  expect(applyRunEvent(run, { schema_version: 1, run_id: 'other', seq: 6 }).kind).toBe('ignore');
  expect(applyRunEvent(run, { schema_version: 1, run_id: 'fixture', seq: NaN }).kind).toBe('recover');
});
