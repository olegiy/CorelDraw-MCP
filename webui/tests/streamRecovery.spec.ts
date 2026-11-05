import { expect, test } from '@playwright/test';

test('gap recovers paginated image history then opens stream at snapshot watermark', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000001';
  const run = { id, conversation_id: '00000000-0000-0000-0000-000000000002', status: 'running', last_seq: 1 };
  let streams = 0;
  let secondPage = false;
  await page.route('**/api/v1/**', async route => {
    const url = new URL(route.request().url()), path = url.pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [run] } });
    if (path.endsWith(`/runs/${id}`)) return route.fulfill({ json: { ...run, last_seq: 502 } });
    if (path.endsWith('/events')) {
      streams++;
      if (streams > 1) { expect(url.searchParams.get('after')).toBe('502'); return route.fulfill({ contentType: 'text/event-stream', body: '' }); }
      return route.fulfill({ contentType: 'text/event-stream', body: `event: update\nid: 4\ndata: ${JSON.stringify({ schema_version: 1, run_id: id, seq: 4, type: 'status', data: { status: 'running' }, state: { status: 'running' } })}\n\n` });
    }
    if (path.endsWith('/event-log')) {
      if (url.searchParams.get('after') === '500') {
        secondPage = true;
        return route.fulfill({ json: { items: [{ type: 'image', data: { image_id: '00000000-0000-0000-0000-000000000003', document_id: 'fixture-b', target: 'page', at: 'now' } }], next_after: null } });
      }
      return route.fulfill({ json: { items: [], next_after: 500 } });
    }
    if (path.includes('/images/')) return route.fulfill({ status: 204 });
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await expect.poll(() => secondPage).toBe(true);
  await expect.poll(() => streams >= 2).toBe(true);
  await expect(page.getByTestId('preview-binding')).toContainText('fixture-b');
  await page.goto('about:blank');
});
