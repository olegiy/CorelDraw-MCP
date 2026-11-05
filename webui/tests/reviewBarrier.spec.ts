import { expect, test } from '@playwright/test';

test('operator barrier review requires explicit manual check and exact selected scope', async ({ page }) => {
  let released = false;
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/health')) return route.fulfill({ json: { safety_barrier: released ? null : { id: '00000000-0000-0000-0000-000000000003', reason: 'CLEANUP_NOT_PROVEN' } } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { stale: false, items: [{ document_id: 'fixture-b', name: 'Рисунок B' }] } });
    if (path.endsWith('/recovery/review')) { const body = route.request().postDataJSON(); expect(body.document_id).toBe('fixture-b'); expect(body.manual_corel_checked).toBe(true); expect(body.barrier_id).toBe('00000000-0000-0000-0000-000000000003'); released = true; return route.fulfill({ json: body }); }
    return route.fulfill({ json: { items: [] } });
  });
  await page.goto('/');
  await expect(page.getByTestId('global-barrier')).toContainText('CLEANUP_NOT_PROVEN');
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await page.getByLabel('Комментарий рассмотрения').fill('Проверил CorelDRAW и диагностику');
  await expect(page.getByRole('button', { name: 'Разрешить новый контекст' })).toBeDisabled();
  await page.getByLabel('Я вручную проверил CorelDRAW и исходную диагностику').check();
  await page.getByRole('button', { name: 'Разрешить новый контекст' }).click();
  await expect.poll(() => released).toBe(true);
  await expect(page.getByTestId('global-barrier')).not.toBeVisible();
});

test('recovery-only UI requires reconcile and explicit host restart before release', async ({ page }) => {
  let reconciled = false;
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/health')) return route.fulfill({ json: { mcp_connected: false, safety_barrier: { id: '00000000-0000-0000-0000-000000000003', reason: 'COREL_OUTCOME_UNKNOWN' } } });
    if (path.endsWith('/recovery/reconcile')) { expect(route.request().postDataJSON()).toEqual({}); reconciled = true; return route.fulfill({ json: { reconciled: true } }); }
    return route.fulfill({ json: { items: [], stale: true } });
  });
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Разрешить новый контекст' })).toBeDisabled();
  await page.getByRole('button', { name: 'Сверить прежние отчёты' }).click();
  await expect.poll(() => reconciled).toBe(true);
  await expect(page.getByTestId('global-barrier')).toContainText('Stop-CorelAgent');
  await expect(page.getByTestId('global-barrier')).toContainText('Start-CorelAgent');
  await expect(page.getByRole('button', { name: 'Разрешить новый контекст' })).toBeDisabled();
  await page.goto('about:blank');
});
