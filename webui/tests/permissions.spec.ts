import { expect, test } from '@playwright/test';

test('ordinary Save and Close are available without permission controls', async ({ page }) => {
  let submitted: Record<string, unknown> | null = null;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B' }], stale: false } });
    if (path.endsWith('/actions')) { submitted = route.request().postDataJSON(); return route.fulfill({ json: { id: crypto.randomUUID(), conversation_id: submitted!.conversation_id, status: 'completed', last_seq: 0 } }); }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [] } });
  });
  await page.goto('/');
  await expect(page.getByLabel('Только чтение')).toHaveCount(0);
  await page.locator('.context-controls .document-disclosure > summary').click();
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await expect(page.getByRole('button', { name: 'Сохранить выбранный CDR', exact: true })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Закрыть документ', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Сохранить выбранный CDR', exact: true }).click();
  await expect.poll(() => submitted?.tool).toBe('corel_save_document');
  expect(submitted!).not.toHaveProperty('read_only');
  expect(submitted!).not.toHaveProperty('output_root_id');
  await expect(page.getByRole('dialog')).not.toBeVisible();
});
