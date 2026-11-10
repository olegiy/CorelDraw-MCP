import { expect, test } from '@playwright/test';

test('restored run shows exact Job and historical cleanup fault without frozen permission', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000001';
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [{ id, conversation_id: id, document_id: 'fixture-b', status: 'running', last_seq: 0 }] } });
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: `event: update\nid: 1\ndata: ${JSON.stringify({ schema_version: 1, run_id: id, seq: 1, type: 'status', data: { status: 'halted' }, state: { status: 'halted', document_id: 'fixture-b', stop_requested: false, job_id: 'job-actual', monitoring_state: 'degraded', safety_barrier: { reason: 'CLEANUP_NOT_PROVEN' } } })}\n\n` });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await expect(page.getByText('Обычная запись разрешена; VBA требует подтверждения')).toHaveCount(0);
  await expect(page.getByLabel('Только чтение')).toHaveCount(0);
  await expect(page.getByTestId('safety-state')).toContainText('CLEANUP_NOT_PROVEN');
  await expect(page.getByTestId('safety-state').getByRole('alert')).toBeVisible();
  await expect(page.locator('.technical-disclosure')).not.toHaveAttribute('open');
  await expect(page.getByTestId('safety-state').getByText('Цель Run:', { exact: false })).not.toBeVisible();
  await page.locator('.technical-disclosure > summary').click();
  await expect(page.getByTestId('safety-state')).toContainText('job-actual');
  await page.locator('.technical-disclosure > summary').click();
  await expect(page.getByTestId('safety-state').getByRole('alert')).toBeVisible();
  await page.goto('about:blank');
});
