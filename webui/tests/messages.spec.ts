import { expect, test } from '@playwright/test';

test('history renders plain text safely and registered download links without remote images', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000001';
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [{ id, conversation_id: id, status: 'running', last_seq: 0 }] } });
    if (path.endsWith('/messages')) return route.fulfill({ json: { items: [
      { id: 'text', role: 'assistant', text: '**Важный ответ**\n\n<script>window.leaked=true</script>\n\n![remote](https://attacker.test/private.png)' },
      { id: 'artifact', role: 'tool', text: JSON.stringify({ artifact_id: id, display_name: 'result.svg', media_type: 'image/svg+xml', bytes: 100 }) },
    ] } });
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await expect(page.getByRole('link', { name: 'Скачать result.svg' })).toHaveAttribute('href', `/api/v1/artifacts/${id}`);
  await expect(page.locator('.messages img')).toHaveCount(0);
  await expect(page.locator('.messages strong').filter({ hasText: 'Важный ответ' })).toBeVisible();
  expect(await page.evaluate(() => (window as Window & { leaked?: boolean }).leaked)).toBeUndefined();
  await page.goto('about:blank');
});
