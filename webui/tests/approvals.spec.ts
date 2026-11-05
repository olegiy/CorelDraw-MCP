import { expect, test } from '@playwright/test';

test('full saved VBA approval is modal and Escape rejects without approving', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000001';
  const conversation = '00000000-0000-0000-0000-000000000002';
  const code = 'Function Main() As String\n' + Array.from({ length: 100 }, (_, index) => `' Строка ${index}`).join('\n') + '\nEnd Function\n\' LAST-LINE-CANARY';
  let decision: { approved: boolean } | null = null;
  const pending = { confirmation_id: '00000000-0000-0000-0000-000000000003', operation_digest: 'a'.repeat(64), reasons: ['VBA имеет полный доступ к ОС.'], operation: { operation_id: id, run_id: id, tool: 'corel_execute_vba', document_id: 'fixture-b', arguments: { code }, digest: 'a'.repeat(64) } };
  const run = { id, conversation_id: conversation, status: 'awaiting_confirmation', last_seq: 8, document_id: 'fixture-b', pending_confirmation: pending };
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [run] } });
    if (path.endsWith('/confirm')) { decision = route.request().postDataJSON(); return route.fulfill({ json: { ...run, status: 'rejected', pending_confirmation: null } }); }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('pre')).toContainText('LAST-LINE-CANARY');
  await expect(dialog).toContainText('fixture-b');
  await page.keyboard.press('Escape');
  await expect.poll(() => decision?.approved).toBe(false);
  await expect(dialog).not.toBeVisible();
});
