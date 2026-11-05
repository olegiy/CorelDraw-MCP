import { expect, test } from '@playwright/test';

test('ordinary write permission is explicit and frozen in submitted command', async ({ page }) => {
  let readonly: boolean | undefined;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B' }], stale: false } });
    if (path.endsWith('/actions')) { const body = route.request().postDataJSON(); readonly = body.read_only; return route.fulfill({ json: { id: '00000000-0000-0000-0000-000000000001', conversation_id: body.conversation_id, status: 'completed', last_seq: 0 } }); }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [] } });
  });
  await page.goto('/');
  await expect(page.getByLabel('Только чтение')).toBeChecked();
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await page.getByLabel('Только чтение').uncheck();
  await page.getByRole('button', { name: 'Сохранить выбранный CDR', exact: true }).click();
  await expect.poll(() => readonly).toBe(false);
  await page.goto('about:blank');
});
