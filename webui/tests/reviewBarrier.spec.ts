import { expect, test } from '@playwright/test';

test('unproven cleanup cannot be released by document selection or permission controls', async ({ page }) => {
  let reviews = 0;
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/health')) return route.fulfill({ json: { mcp_connected: true, readiness_reason: 'CLEANUP_NOT_PROVEN', safety_barrier: { id: '00000000-0000-0000-0000-000000000003', reason: 'CLEANUP_NOT_PROVEN' } } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { stale: false, items: [{ document_id: 'fixture-b', name: 'Рисунок B' }] } });
    if (path.endsWith('/recovery/review')) { reviews++; return route.fulfill({ status: 404, json: {} }); }
    return route.fulfill({ json: { items: [] } });
  });
  await page.goto('/');
  await expect(page.getByTestId('global-barrier')).toContainText('CLEANUP_NOT_PROVEN');
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await expect(page.getByLabel('Комментарий рассмотрения')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Разрешить новый контекст' })).toHaveCount(0);
  await expect(page.getByTestId('global-barrier')).toContainText('CLEANUP_NOT_PROVEN');
  expect(reviews).toBe(0);
});

test('recovery-only UI requires reconcile and explicit host restart before automatic readiness', async ({ page }) => {
  let reconciled = false;
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/health')) return route.fulfill({ json: { mcp_connected: false, safety_barrier: { id: '00000000-0000-0000-0000-000000000003', reason: 'COREL_OUTCOME_UNKNOWN' } } });
    if (path.endsWith('/recovery/reconcile')) { expect(route.request().postDataJSON()).toEqual({}); reconciled = true; return route.fulfill({ json: { reconciled: true } }); }
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Разрешить новый контекст' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Сверить прежние отчёты' }).click();
  await expect.poll(() => reconciled).toBe(true);
  await expect(page.getByTestId('global-barrier')).toContainText('Stop-CorelAgent');
  await expect(page.getByTestId('global-barrier')).toContainText('Start-CorelAgent');
  await expect(page.getByRole('button', { name: 'Разрешить новый контекст' })).toHaveCount(0);
  await expect(page.getByTestId('global-barrier')).toContainText('Readiness проверяется автоматически');
  await page.goto('about:blank');
});
