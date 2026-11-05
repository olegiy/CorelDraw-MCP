import { expect, test } from '@playwright/test';

test('live native stage warning and assistant message update before terminal status', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000001';
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [{ id, conversation_id: id, status: 'running', last_seq: 0 }] } });
    if (path.endsWith('/events')) {
      const state = { status: 'running', document_id: 'fixture-b', job_id: 'job-real', monitoring_state: 'degraded', stop_requested: false };
      const items = [{ seq: 1, type: 'job_progress', data: { stage: 'executing_vba', status: 'timed_out', job_id: 'job-real' } },
        { seq: 2, type: 'warning', data: { code: 'DEADLINE_EXCEEDED', message: 'Срок наблюдения истёк; операция продолжается.' } },
        { seq: 3, type: 'message', data: { id, role: 'assistant', text: 'Текущий ответ до завершения' } }];
      return route.fulfill({ contentType: 'text/event-stream', body: items.map(item => `event: update\nid: ${item.seq}\ndata: ${JSON.stringify({ schema_version: 1, run_id: id, ...item, state })}\n\n`).join('') });
    }
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await expect(page.getByTestId('job-stage')).toContainText('executing_vba');
  await expect(page.getByTestId('job-stage')).toContainText('timed_out');
  await expect(page.getByRole('alert').filter({ hasText: 'Срок наблюдения истёк' })).toBeVisible();
  await expect(page.locator('.messages')).toContainText('Текущий ответ до завершения');
  await page.goto('about:blank');
});
