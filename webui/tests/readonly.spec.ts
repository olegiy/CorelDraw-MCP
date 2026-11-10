import { expect, test } from '@playwright/test';

test('documents and exact selection preview require no read-only mode', async ({ page }) => {
  let target = 'page';
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/actions')) {
      const body = route.request().postDataJSON();
      target = body.arguments?.target ?? 'page';
      await route.fulfill({ json: { id: '00000000-0000-0000-0000-000000000001', conversation_id: body.conversation_id, status: 'preparing', last_seq: 0, document_id: body.document_id } });
    } else if (path.endsWith('/events')) {
      const state = { status: 'completed' };
      const events = [
        { seq: 1, type: 'image', data: { image_id: '00000000-0000-0000-0000-000000000002', document_id: 'fixture-b', target, width: 10, height: 10, at: '2026-11-04T00:00:00Z' }, state },
        { seq: 2, type: 'status', data: { status: 'completed' }, state },
      ];
      await route.fulfill({ contentType: 'text/event-stream', body: events.map(event => `event: update\nid: ${event.seq}\ndata: ${JSON.stringify({ schema_version: 1, run_id: '00000000-0000-0000-0000-000000000001', ...event })}\n\n`).join('') });
    } else if (path.includes('/images/')) {
      await route.fulfill({ contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAoAAAAKCAIAAAACUFjqAAAAFklEQVR4nGP8//8/A27AhEeOYeRKAwCl4wMRx3ocVQAAAABJRU5ErkJggg==', 'base64') });
    } else if (path.endsWith('/documents')) {
      await route.fulfill({ json: { items: [{ document_id: 'fixture-a', name: 'Рисунок A', active_page: 1 }, { document_id: 'fixture-b', name: 'Рисунок B', active_page: 1 }], at: '2026-11-04T00:00:00Z', stale: false } });
    } else {
      await route.fulfill({ json: { items: [] } });
    }
  });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'CorelDRAW Agent' })).toBeVisible();
  await expect(page.getByLabel('Только чтение')).toHaveCount(0);
  await page.locator('.context-controls .document-disclosure > summary').click();
  await page.getByRole('button', { name: 'Обновить документы' }).click();
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await page.getByRole('button', { name: 'Превью выделения' }).click();
  await expect(page.getByRole('img', { name: 'Превью CorelDRAW' })).toBeVisible();
  await expect(page.getByTestId('preview-binding')).toContainText('fixture-b');
  await expect(page.getByTestId('preview-binding')).toContainText('selection');
  await page.goto('about:blank');
});

for (const [name, tool, args] of [
  ['Инспекция', 'corel_inspect_document', {}],
  ['Сохранить выбранный CDR', 'corel_save_document', {}],
  ['Закрыть документ', 'corel_close_document', {}],
  ['Превью страницы', 'corel_preview', { target: 'page' }],
  ['Превью выделения', 'corel_preview', { target: 'selection' }],
] as const) {
  test(`document disclosure dispatches ${name} exactly once with unchanged target and arguments`, async ({ page }) => {
    const submitted: Record<string, unknown>[] = [];
    await page.route('**/api/v1/**', route => {
      const path = new URL(route.request().url()).pathname;
      if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
      if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B' }], stale: false } });
      if (path.endsWith('/actions')) { const body = route.request().postDataJSON(); submitted.push(body); return route.fulfill({ json: { id: crypto.randomUUID(), conversation_id: body.conversation_id, document_id: body.document_id, status: 'completed', last_seq: 0 } }); }
      if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
      return route.fulfill({ json: { items: [] } });
    });
    await page.goto('/');
    const document = page.locator('.context-controls .document-disclosure');
    await document.locator(':scope > summary').click();
    await document.getByLabel('Целевой документ', { exact: true }).selectOption('fixture-b');
    await expect(document.getByRole('button', { name, exact: true })).toHaveCount(1);
    await document.getByRole('button', { name, exact: true }).click();
    await expect(page.getByRole('status')).toContainText('completed');
    expect(submitted).toHaveLength(1);
    expect(submitted[0]).toMatchObject({ tool, document_id: 'fixture-b', arguments: args });
    expect(submitted[0].arguments).toEqual(args);
  });
}
