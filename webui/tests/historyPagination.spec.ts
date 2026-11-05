import { expect, test } from '@playwright/test';

test('history follows operation and message next offsets without replaying commands', async ({ page }) => {
  let messagePage = 0; let operationPage = 0; let posts = 0;
  await page.route('**/api/v1/**', async route => {
    const url = new URL(route.request().url()), path = url.pathname;
    if (route.request().method() === 'POST') posts++;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [{ id: '00000000-0000-0000-0000-000000000001', conversation_id: '00000000-0000-0000-0000-000000000002', status: 'running', last_seq: 1 }] } });
    if (path.endsWith('/messages')) { messagePage++; return route.fulfill({ json: { items: [{ id: `m${messagePage}`, role: 'assistant', text: `page ${messagePage}` }], next_offset: messagePage < 2 ? 1 : null } }); }
    if (path.endsWith('/operations')) { operationPage++; return route.fulfill({ json: { items: [], next_offset: operationPage < 2 ? 1 : null } }); }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await expect.poll(() => messagePage).toBe(2);
  await expect(page.getByText('page 1')).toBeVisible();
  await expect.poll(() => operationPage).toBe(2);
  expect(posts).toBe(0);
  await page.goto('about:blank');
});
