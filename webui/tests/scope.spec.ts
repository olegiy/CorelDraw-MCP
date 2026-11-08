import { expect, test } from '@playwright/test';

test('saved external provider and exact Save destination are visible before dispatch', async ({ page }) => {
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/profiles')) return route.fulfill({ json: { items: [{ id: '00000000-0000-0000-0000-000000000001', name: 'External', model: 'external-model', base_url: 'https://chosen.example/v1' }] } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { stale: false, items: [{ document_id: 'fixture-b', name: 'Рисунок B', path: 'C:\\drawings\\selected.cdr', active_page: 2 }] } });
    return route.fulfill({ json: { items: [] } });
  });
  await page.goto('/');
  await expect(page.getByTestId('provider-scope')).toContainText('https://chosen.example/v1');
  await expect(page.getByTestId('provider-scope')).toContainText('external-model');
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await expect(page.getByTestId('save-destination')).toContainText('C:\\drawings\\selected.cdr');
  await page.getByRole('button', { name: 'Настройки модели' }).click();
  await expect(page.getByLabel('Модель', { exact: true })).toHaveValue('external-model');
  await expect(page.getByLabel('API URL', { exact: true })).toHaveValue('https://chosen.example/v1');
  await expect(page.getByLabel('API key', { exact: true })).toHaveValue('');
});
