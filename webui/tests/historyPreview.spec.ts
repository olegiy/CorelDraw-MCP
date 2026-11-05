import { expect, test } from '@playwright/test';

test('switching completed history restores its own preview and clears the previous image', async ({ page }) => {
  const first = '00000000-0000-0000-0000-000000000001';
  const second = '00000000-0000-0000-0000-000000000002';
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [first, second].map(id => ({ id, conversation_id: id, kind: 'manual', status: 'completed', last_seq: 10 })) } });
    if (path.endsWith('/event-log')) {
      const id = path.includes(first) ? first : second;
      return route.fulfill({ json: { items: [{ type: 'image', data: { image_id: id, document_id: id === first ? 'document-A' : 'document-B', target: 'page', at: '2026-11-05T00:00:00Z' } }], next_after: null } });
    }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    if (path.includes('/images/')) return route.fulfill({ status: 204 });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  const history = page.getByRole('heading', { name: 'История' }).locator('..');
  await history.getByRole('button', { name: 'manual · completed', exact: true }).nth(0).click();
  await expect(page.getByTestId('preview-binding')).toContainText('document-A');
  await history.getByRole('button', { name: 'manual · completed', exact: true }).nth(1).click();
  await expect(page.getByTestId('preview-binding')).toContainText('document-B');
  await expect(page.getByTestId('preview-binding')).not.toContainText('document-A');
  await page.goto('about:blank');
});
