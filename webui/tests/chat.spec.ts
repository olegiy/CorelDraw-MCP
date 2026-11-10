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
  await page.locator('.context-controls .document-disclosure > summary').click();
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await expect(page.getByRole('button', { name: 'Отправить', exact: true })).toBeDisabled();
  await expect(page.getByLabel('Сообщение агенту', { exact: true })).toHaveAttribute('maxlength', '65536');
  await page.getByLabel('Сообщение агенту').fill('Покажи рисунок');
  await page.getByRole('button', { name: 'Отправить' }).click();
  await expect.poll(() => submitted).toBe(true);
  await expect(page.getByRole('status')).toContainText('completed');
  await expect(page.getByLabel('Сообщение агенту', { exact: true })).toHaveValue('');
  await page.goto('about:blank');
});

test('keyboard composer preserves newlines, trims one submission and disables while pending or active', async ({ page }) => {
  const submitted: string[] = [];
  let release!: () => void;
  const acceptance = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { csrf_token: 'b'.repeat(43), authenticated: true } });
    if (path.endsWith('/runs') && route.request().method() === 'POST') {
      const body = route.request().postDataJSON();
      submitted.push(body.message);
      await acceptance;
      return route.fulfill({ json: { id: '00000000-0000-0000-0000-000000000001', conversation_id: body.conversation_id, kind: 'agent', status: 'running', last_seq: 0 } });
    }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  try {
    await page.goto('/');
    const message = page.getByLabel('Сообщение агенту', { exact: true });
    const send = page.getByRole('button', { name: 'Отправить', exact: true });
    await message.fill('   ');
    await expect(send).toBeDisabled();
    await message.press('Enter');
    expect(submitted).toEqual([]);
    await message.fill('  Нарисуй круг');
    await message.press('End');
    await message.press('Shift+Enter');
    await message.pressSequentially('Затем сохрани  ');
    await expect(message).toHaveValue('  Нарисуй круг\nЗатем сохрани  ');
    expect(submitted).toEqual([]);
    await expect(send).toBeEnabled();
    await message.press('Enter');
    await expect.poll(() => submitted).toEqual(['Нарисуй круг\nЗатем сохрани']);
    await expect(message).toHaveValue('');
    await expect(page.getByRole('status')).toContainText('ожидание принятия запроса');
    await expect(message).toBeDisabled();
    await expect(send).toBeDisabled();
    release();
    await expect(page.getByRole('status')).toContainText('running');
    await expect(message).toBeDisabled();
    await expect(send).toBeDisabled();
    expect(submitted).toHaveLength(1);
  } finally {
    release();
    await page.goto('about:blank');
  }
});

for (const width of [390, 1280]) {
  test(`composer has a single bordered surface and accessible keyboard guidance at ${width}px`, async ({ page }) => {
    await page.route('**/api/v1/**', route => {
      const path = new URL(route.request().url()).pathname;
      if (path.endsWith('/access/session')) return route.fulfill({ json: { csrf_token: 'b'.repeat(43), authenticated: true } });
      return route.fulfill({ json: { items: [], stale: true } });
    });
    await page.setViewportSize({ width, height: 844 });
    await page.goto('/');
    const composer = page.locator('.chat-surface .chat-composer');
    const message = composer.getByLabel('Сообщение агенту', { exact: true });
    const send = composer.getByRole('button', { name: 'Отправить', exact: true });
    await expect(composer).toHaveCSS('border-top-width', '1px');
    await expect(message).toHaveCSS('border-top-width', '0px');
    await expect(message).toHaveAttribute('aria-describedby', 'agent-message-hint');
    await expect(message).toHaveAccessibleDescription('Enter: отправить · Shift+Enter: новая строка');
    await expect(send).toHaveAttribute('type', 'submit');
    await expect(send).toBeVisible();
    await message.fill('Проверь рисунок');
    await message.press('Tab');
    await expect(send).toBeFocused();
    expect(await message.evaluate(element => element.getBoundingClientRect().height)).toBeLessThan(110);
    expect(await composer.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.goto('about:blank');
  });
}
