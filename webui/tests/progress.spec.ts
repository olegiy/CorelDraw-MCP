import { expect, test } from '@playwright/test';
import { expectStatusAndStopInViewport } from './stopViewport';

for (const width of [390, 1280]) {
  test(`I1 active Run keeps status and actual Stop in viewport with open document and file forms at ${width}px`, async ({ page }) => {
    const historyId = '00000000-0000-0000-0000-000000000071';
    const id = '00000000-0000-0000-0000-000000000072';
    const old = { id: historyId, conversation_id: historyId, kind: 'manual', status: 'completed', last_seq: 0, document_id: 'fixture-b' };
    const active = { ...old, id, status: 'running', job_id: 'job-active', last_seq: 0 };
    let stopped = false;
    const posts: { path: string; body: Record<string, unknown> | null }[] = [];
    await page.route('**/api/v1/**', route => {
      const path = new URL(route.request().url()).pathname;
      if (route.request().method() === 'POST') posts.push({ path, body: route.request().postDataJSON() });
      if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
      if (path.endsWith('/health')) return route.fulfill({ json: { mcp_connected: true, safety_barrier: null, readiness_reason: null } });
      if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B' }], stale: false } });
      if (path.endsWith('/runs')) return route.fulfill({ json: { items: [old], next_offset: null } });
      if (path.endsWith('/messages')) return route.fulfill({ json: { items: Array.from({ length: 80 }, (_, index) => ({ id: `message-${index}`, role: 'assistant', text: `Сообщение ${index}. Длинная история выполняемой операции.` })), next_offset: null } });
      if (path.endsWith('/actions')) return route.fulfill({ json: active });
      if (path.endsWith(`/runs/${id}/stop`)) { stopped = true; return route.fulfill({ json: { ...active, status: 'stopped', stop_requested: true, last_seq: 2 } }); }
      if (path.endsWith(`/runs/${id}`)) return route.fulfill({ json: { ...active, ...(stopped ? { status: 'stopped', stop_requested: true, last_seq: 2 } : {}) } });
      if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: `event: update\nid: 1\ndata: ${JSON.stringify({ schema_version: 1, run_id: id, seq: 1, type: 'job_progress', data: { stage: 'executing_vba', status: 'running', job_id: 'job-active' }, state: { status: 'running', document_id: 'fixture-b', job_id: 'job-active', monitoring_state: 'healthy', stop_requested: false } })}\n\n` });
      return route.fulfill({ json: { items: [], output_directory: null } });
    });
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
    await files.getByLabel('Имя результата', { exact: true }).fill('C:\\exports\\keep.svg');
    await document.getByRole('button', { name: 'Превью страницы', exact: true }).click();
    await expect(page.locator('.context-bar').getByRole('status')).toContainText('running');
    await expect(document).toHaveAttribute('open', '');
    await expect(files).toHaveAttribute('open', '');
    await page.locator('.chat-surface .technical-disclosure > summary').focus();
    await page.keyboard.press('PageDown');
    await page.keyboard.press('PageDown');
    await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(844);
    await expectStatusAndStopInViewport(page);
    expect(await page.locator('.chat-surface .technical-disclosure > summary').evaluate(element => element.getBoundingClientRect().bottom)).toBeLessThanOrEqual(0);
    await page.locator('.messages article').last().scrollIntoViewIfNeeded();
    const bounds = await expectStatusAndStopInViewport(page);
    await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    await expect(page.getByRole('status')).toContainText('stopped');
    expect(posts).toHaveLength(2);
    expect(posts[0]).toMatchObject({ path: '/api/v1/actions', body: { tool: 'corel_preview', document_id: 'fixture-b', arguments: { target: 'page' } } });
    expect(posts[1].path).toBe(`/api/v1/runs/${id}/stop`);
    await expect(document).toHaveAttribute('open', '');
    await expect(files).toHaveAttribute('open', '');
    await expect(document.getByLabel('Целевой документ', { exact: true })).toHaveValue('fixture-b');
    await expect(files.getByLabel('Открыть локальный CDR', { exact: true })).toHaveValue('C:\\drafts\\keep.cdr');
    await expect(files.getByLabel('Имя результата', { exact: true })).toHaveValue('C:\\exports\\keep.svg');
    await page.goto('about:blank');
  });
}

test('live native stage warning and assistant message update before terminal status', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000001';
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [{ id, conversation_id: id, status: 'running', last_seq: 0 }] } });
    if (path.endsWith('/events')) {
      const state = { status: 'running', document_id: 'fixture-b', job_id: 'job-real', monitoring_state: 'degraded', stop_requested: false };
      const items = [{ seq: 1, type: 'job_progress', data: { stage: 'executing_vba', status: 'timed_out', job_id: 'job-real' } },
        { seq: 2, type: 'warning', data: { code: 'DEADLINE_EXCEEDED', message: 'Срок наблюдения истёк; операция продолжается.' } },
        { seq: 3, type: 'message', data: { id, role: 'assistant', text: 'Текущий ответ до завершения' } },
        { seq: 4, type: 'image', data: { image_id: id, document_id: 'fixture-b', target: 'page', at: '2026-11-05T00:00:00Z' } }];
      return route.fulfill({ contentType: 'text/event-stream', body: items.map(item => `event: update\nid: ${item.seq}\ndata: ${JSON.stringify({ schema_version: 1, run_id: id, ...item, state })}\n\n`).join('') });
    }
    if (path.includes('/images/')) return route.fulfill({ status: 204 });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await page.locator('.chat-surface .technical-disclosure > summary').click();
  await expect(page.getByTestId('job-stage')).toContainText('executing_vba');
  await expect(page.getByTestId('job-stage')).toContainText('timed_out');
  await page.locator('.chat-surface .technical-disclosure > summary').click();
  await expect(page.getByRole('alert').filter({ hasText: 'Срок наблюдения истёк' })).toBeVisible();
  await expect(page.locator('.messages')).toContainText('Текущий ответ до завершения');
  const message = page.locator('.messages article[data-role="assistant"]');
  await expect(message).toHaveClass(/\bmessage-assistant\b/);
  await expect(message.locator('strong').first()).toHaveText('assistant');
  await expect(page.locator('.chat-surface .preview-result').getByTestId('preview-binding')).toHaveText('fixture-b · page · 2026-11-05T00:00:00Z. Сохранённое превью, не live-canvas.');
  await expect(page.getByAltText('Превью CorelDRAW', { exact: true })).toHaveAttribute('src', `/api/v1/images/${id}`);
  await expect(page.getByLabel('Сообщение агенту', { exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Отправить', exact: true })).toBeDisabled();
  await expect(page.locator('.context-bar').getByRole('button', { name: 'Стоп', exact: true })).toBeVisible();
  await page.goto('about:blank');
});
