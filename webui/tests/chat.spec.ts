import { expect, test } from '@playwright/test';

test('chat submits selected profile document and CSRF with native agent command', async ({ page }) => {
  let submitted = false;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { csrf_token: 'b'.repeat(43), authenticated: true } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B', active_page: 1 }], stale: false } });
    if (path.endsWith('/profiles')) return route.fulfill({ json: { items: [{ id: '00000000-0000-0000-0000-000000000003', name: 'Модель', model: 'fake', base_url: 'http://127.0.0.1:1234/v1' }] } });
    if (path.endsWith('/runs') && route.request().method() === 'POST') {
      const body = route.request().postDataJSON();
      expect(body.message).toBe('Покажи рисунок');
      expect(body.document_id).toBe('fixture-b');
      expect(route.request().headers()['x-corel-csrf']).toBe('b'.repeat(43));
      submitted = true;
      return route.fulfill({ json: { id: '00000000-0000-0000-0000-000000000001', conversation_id: body.conversation_id, status: 'completed', last_seq: 0 } });
    }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [] } });
  });
  await page.goto('/');
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await page.getByLabel('Сообщение агенту').fill('Покажи рисунок');
  await page.getByRole('button', { name: 'Отправить' }).click();
  await expect.poll(() => submitted).toBe(true);
  await expect(page.getByRole('status')).toContainText('completed');
  await page.goto('about:blank');
});
