import { expect, test } from '@playwright/test';

test('reload restores saved run without submitting new command or COM refresh', async ({ page }) => {
  let posts = 0;
  const run = { id: '00000000-0000-0000-0000-000000000001', conversation_id: '00000000-0000-0000-0000-000000000002', status: 'awaiting_confirmation', last_seq: 8, document_id: 'fixture-b', pending_confirmation: null };
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (route.request().method() === 'POST') posts++;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B', active_page: 2 }], stale: false } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [run] } });
    if (path.endsWith(`/runs/${run.id}`)) return route.fulfill({ json: run });
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [] } });
  });
  await page.goto('/');
  await expect(page.getByRole('status')).toContainText('awaiting_confirmation');
  await page.reload();
  await expect(page.getByRole('status')).toContainText('awaiting_confirmation');
  expect(posts).toBe(0);
  await page.goto('about:blank');
});
