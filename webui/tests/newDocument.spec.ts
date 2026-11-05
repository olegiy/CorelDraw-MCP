import { expect, test } from '@playwright/test';

test('agent new document requires explicit intent and write permission', async ({ page }) => {
  let accepted = false;
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/profiles')) return route.fulfill({ json: { items: [{ id: '00000000-0000-0000-0000-000000000001', name: 'Fixture', model: 'fixture', base_url: 'http://127.0.0.1:1/v1', context_approved: true, local: true }] } });
    if (path.endsWith('/runs') && route.request().method() === 'POST') { const body = route.request().postDataJSON(); expect(body.new_document).toBe(true); expect(body.document_id).toBeNull(); expect(body.read_only).toBe(false); accepted = true; return route.fulfill({ json: { id: '00000000-0000-0000-0000-000000000002', conversation_id: body.conversation_id, status: 'completed', last_seq: 0 } }); }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await expect(page.getByLabel('Агент создаёт новый документ')).toBeDisabled();
  await page.getByLabel('Только чтение').uncheck();
  await page.getByLabel('Агент создаёт новый документ').check();
  await page.getByLabel('Сообщение агенту').fill('Создай новый рисунок');
  await page.getByRole('button', { name: 'Отправить', exact: true }).click();
  await expect.poll(() => accepted).toBe(true);
  await page.goto('about:blank');
});
