import { expect, test } from '@playwright/test';
import { expectStatusAndStopInViewport } from './stopViewport';

for (const width of [390, 1280]) {
  test(`I1 pending acceptance keeps status and keyboard Stop in viewport with open document and file forms at ${width}px`, async ({ page }) => {
    const id = '00000000-0000-0000-0000-000000000081';
    const old = { id, conversation_id: id, kind: 'manual', status: 'completed', last_seq: 0, document_id: 'fixture-b' };
    let requestId = ''; let stopId = ''; let stopCount = 0;
    let release: () => void = () => {};
    const gate = new Promise<void>(resolve => { release = resolve; });
    await page.route('**/api/v1/**', async route => {
      const path = new URL(route.request().url()).pathname;
      if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
      if (path.endsWith('/health')) return route.fulfill({ json: { mcp_connected: true, safety_barrier: null, readiness_reason: null } });
      if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B' }], stale: false } });
      if (path.endsWith('/runs')) return route.fulfill({ json: { items: [old], next_offset: null } });
      if (path.endsWith('/messages')) return route.fulfill({ json: { items: Array.from({ length: 80 }, (_, index) => ({ id: `message-${index}`, role: 'assistant', text: `Сообщение ${index}. Длинная история до принятия следующего запроса.` })), next_offset: null } });
      if (path.endsWith('/actions')) { const body = route.request().postDataJSON(); requestId = body.client_request_id; await gate; return route.fulfill({ json: { ...old, id: '00000000-0000-0000-0000-000000000082', status: 'stopped', stop_requested: true, last_seq: 1 } }); }
      if (path.startsWith('/api/v1/requests/') && path.endsWith('/stop')) { stopCount++; stopId = path.split('/')[4]; release(); return route.fulfill({ json: null }); }
      if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
      return route.fulfill({ json: { items: [], output_directory: null } });
    });
    try {
      await page.setViewportSize({ width, height: 844 });
      await page.goto('/');
      await page.locator('.conversation-list').getByRole('button', { name: 'manual · completed', exact: true }).click();
      await expect(page.locator('.messages article')).toHaveCount(80);
      const document = page.locator('.context-actions .document-disclosure');
      const files = page.locator('.context-actions .file-disclosure');
      await document.locator(':scope > summary').focus();
      await page.keyboard.press('Enter');
      await files.locator(':scope > summary').focus();
      await page.keyboard.press('Space');
      await files.getByLabel('Открыть локальный CDR', { exact: true }).fill('C:\\drafts\\keep.cdr');
      await document.getByRole('button', { name: 'Обновить документы', exact: true }).click();
      await expect.poll(() => requestId.length > 0).toBe(true);
      await expect(page.locator('.context-bar').getByRole('status')).toContainText('ожидание принятия запроса');
      await expect(document).toHaveAttribute('open', '');
      await expect(files).toHaveAttribute('open', '');
      await page.locator('.chat-surface .technical-disclosure > summary').focus();
      await page.keyboard.press('PageDown');
      await page.keyboard.press('PageDown');
      await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(844);
      await expectStatusAndStopInViewport(page);
      expect(await page.locator('.chat-surface .technical-disclosure > summary').evaluate(element => element.getBoundingClientRect().bottom)).toBeLessThanOrEqual(0);
      await page.locator('.messages article').last().scrollIntoViewIfNeeded();
      await expectStatusAndStopInViewport(page);
      const stop = page.locator('.context-bar').getByRole('button', { name: 'Стоп', exact: true });
      const scrollBeforeFocus = await page.evaluate(() => scrollY);
      await stop.focus();
      expect(await page.evaluate(() => scrollY)).toBe(scrollBeforeFocus);
      await expect(stop).toBeFocused();
      await page.keyboard.press('Enter');
      await expect(page.getByRole('status')).toContainText('stopped');
      expect(stopId).toBe(requestId);
      expect(stopCount).toBe(1);
      await expect(document).toHaveAttribute('open', '');
      await expect(files).toHaveAttribute('open', '');
      await expect(files.getByLabel('Открыть локальный CDR', { exact: true })).toHaveValue('C:\\drafts\\keep.cdr');
    } finally { release(); await page.goto('about:blank'); }
  });
}

test('Stop while POST acceptance is pending uses original request ID tombstone', async ({ page }) => {
  let requestId = ''; let stopId = '';
  let release: () => void = () => {};
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/actions')) { const body = route.request().postDataJSON(); requestId = body.client_request_id; await gate; return route.fulfill({ json: { id: '00000000-0000-0000-0000-000000000001', conversation_id: body.conversation_id, status: 'stopped', last_seq: 1 } }); }
    if (path.startsWith('/api/v1/requests/') && path.endsWith('/stop')) { stopId = path.split('/')[4]; release(); return route.fulfill({ json: null }); }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await page.locator('.context-controls .document-disclosure > summary').click();
  await page.getByRole('button', { name: 'Обновить документы' }).click();
  await expect.poll(() => requestId.length > 0).toBe(true);
  await page.getByRole('button', { name: 'Стоп', exact: true }).click();
  await expect.poll(() => stopId).toBe(requestId);
  await expect(page.getByRole('status')).toContainText('stopped');
  await page.goto('about:blank');
});
