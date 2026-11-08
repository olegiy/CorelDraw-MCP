import { expect, test } from '@playwright/test';

test('historical approval is informational and never opens a dialog or confirms', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000001';
  const conversation = '00000000-0000-0000-0000-000000000002';
  const pending = { confirmation_id: crypto.randomUUID(), operation_digest: 'a'.repeat(64), reasons: ['Legacy VBA'], operation: { operation_id: id, run_id: id, tool: 'corel_execute_vba', arguments: { code: 'legacy code' }, digest: 'a'.repeat(64) } };
  const run = { id, kind: 'manual', conversation_id: conversation, status: 'interrupted', last_seq: 8, document_id: 'fixture-b', pending_confirmation: pending };
  let confirms = 0;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [run] } });
    if (path.endsWith('/confirm')) { confirms++; return route.fulfill({ status: 404, json: {} }); }
    if (path.endsWith('/event-log')) return route.fulfill({ json: { items: [{ run_id: id, seq: 8, type: 'warning', state: { status: 'awaiting_confirmation' }, data: { code: 'LEGACY_CONFIRMATION', message: 'Историческое подтверждение не возобновляется.' } }] } });
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await page.getByRole('button', { name: 'manual · interrupted', exact: true }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible();
  await expect(page.getByText(/LEGACY_CONFIRMATION: Историческое/)).toBeVisible();
  await page.keyboard.press('Escape');
  expect(confirms).toBe(0);
});
