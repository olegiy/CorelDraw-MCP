import { expect, test } from '@playwright/test';

test('run history follows all pages and New conversation gets a fresh ID without POST', async ({ page }) => {
  let pageNumber = 0; let posts = 0;
  await page.route('**/api/v1/**', route => {
    const url = new URL(route.request().url()), path = url.pathname;
    if (route.request().method() === 'POST') posts++;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/runs')) { pageNumber++; return route.fulfill({ json: { items: [{ id: `00000000-0000-0000-0000-00000000000${pageNumber}`, kind: 'manual', conversation_id: '00000000-0000-0000-0000-000000000099', status: 'completed', last_seq: 1 }], next_offset: pageNumber < 2 ? 1 : null } }); }
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await expect.poll(() => pageNumber).toBe(2);
  await expect(page.getByRole('button', { name: /manual · completed/ })).toHaveCount(2);
  await page.getByRole('button', { name: 'Новая переписка' }).click();
  expect(posts).toBe(0);
  await page.goto('about:blank');
});
