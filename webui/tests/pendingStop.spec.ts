import { expect, test } from '@playwright/test';

test('Stop while POST acceptance is pending uses original request ID tombstone', async ({ page }) => {
  let requestId = ''; let stopId = '';
  let release: () => void = () => {};
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/actions')) { const body = route.request().postDataJSON(); requestId = body.client_request_id; await gate; return route.fulfill({ json: { id: '00000000-0000-0000-0000-000000000001', conversation_id: body.conversation_id, status: 'stopped', last_seq: 1 } }); }
    if (path.startsWith('/api/v1/requests/') && path.endsWith('/stop')) { stopId = path.split('/')[4]; release(); return route.fulfill({ json: null }); }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await page.getByRole('button', { name: 'Обновить документы' }).click();
  await expect.poll(() => requestId.length > 0).toBe(true);
  await page.getByRole('button', { name: 'Стоп', exact: true }).click();
  await expect.poll(() => stopId).toBe(requestId);
  await expect(page.getByRole('status')).toContainText('stopped');
  await page.goto('about:blank');
});
