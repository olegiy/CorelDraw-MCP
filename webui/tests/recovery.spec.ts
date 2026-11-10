import { expect, test } from '@playwright/test';

test('reload restores saved run without submitting new command or COM refresh', async ({ page }) => {
  let posts = 0;
  const run = { id: '00000000-0000-0000-0000-000000000001', conversation_id: '00000000-0000-0000-0000-000000000002', status: 'running', last_seq: 8, document_id: 'fixture-b' };
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (route.request().method() === 'POST') posts++;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B', active_page: 2 }], stale: false } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [run] } });
    if (path.endsWith(`/runs/${run.id}`)) return route.fulfill({ json: run });
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [] } });
  });
  await page.goto('/');
  await expect(page.getByRole('status')).toContainText('running');
  await page.reload();
  await expect(page.getByRole('status')).toContainText('running');
  expect(posts).toBe(0);
  await page.goto('about:blank');
});

test('readiness is automatic after refresh without review controls or rewriting old outcome', async ({ page }) => {
  const barrier = { id: '00000000-0000-0000-0000-000000000004', reason: 'RUNTIME_INTERRUPTED' };
  const old = { id: '00000000-0000-0000-0000-000000000001', conversation_id: '00000000-0000-0000-0000-000000000002', status: 'halted', last_seq: 8, document_id: 'fixture-b', safety_barrier: barrier };
  const refresh = { ...old, id: '00000000-0000-0000-0000-000000000003', status: 'completed', last_seq: 1 };
  let refreshed = false; let reviews = 0;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/recovery/review')) { reviews++; return route.fulfill({ status: 404, json: {} }); }
    if (path.endsWith('/health')) return route.fulfill({ json: { mcp_connected: true, store_ready: true, safety_barrier: refreshed ? null : barrier, readiness_reason: refreshed ? null : 'CLEANUP_NOT_PROVEN' } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B' }], stale: !refreshed } });
    if (path.endsWith('/actions')) { expect(route.request().postDataJSON().tool).toBe('corel_status'); refreshed = true; return route.fulfill({ json: refresh }); }
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [old] } });
    if (path.endsWith(`/runs/${refresh.id}`)) return route.fulfill({ json: refresh });
    if (path.endsWith(`/runs/${old.id}`)) return route.fulfill({ json: old });
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [] } });
  });
  await page.goto('/');
  await expect(page.getByTestId('global-barrier')).toBeVisible();
  await expect(page.getByTestId('global-barrier')).toContainText('нажмите «Обновить документы»');
  await expect(page.getByLabel('Комментарий рассмотрения')).toHaveCount(0);
  await expect(page.getByText('Я вручную проверил CorelDRAW и исходную диагностику', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Разрешить новый контекст', exact: true })).toHaveCount(0);
  await page.locator('.context-controls .document-disclosure > summary').click();
  await page.getByRole('button', { name: 'Обновить документы', exact: true }).click();
  await expect(page.getByTestId('global-barrier')).toHaveCount(0);
  expect(old.status).toBe('halted'); expect(reviews).toBe(0);
});
