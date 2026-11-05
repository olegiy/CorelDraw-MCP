import { expect, test } from '@playwright/test';

test('settings remains modal on vault failure and Escape clears credentials', async ({ page }) => {
  let saved: Record<string, unknown> | null = null;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (route.request().method() === 'PUT') {
      saved = route.request().postDataJSON();
      return route.fulfill({ status: 503, json: { code: 'VAULT_UNAVAILABLE', message: 'Credential Manager недоступен.' } });
    }
    return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43), items: [], stale: true } });
  });
  await page.goto('/');
  await page.getByRole('button', { name: 'Настройки модели' }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  expect(await page.getByRole('dialog').evaluate(element => element.matches(':modal'))).toBe(true);
  await page.getByLabel('Модель', { exact: true }).fill('fixture');
  await page.getByLabel('API key', { exact: true }).fill('CANARY-PRIVATE');
  await page.getByRole('button', { name: 'Сохранить в Credential Manager' }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Credential Manager');
  await expect.poll(() => saved).not.toBeNull();
  expect((saved!.profile as { context_approved: boolean }).context_approved).toBe(false);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).not.toBeVisible();
  await page.getByRole('button', { name: 'Настройки модели' }).click();
  await expect(page.getByLabel('API key', { exact: true })).toHaveValue('');
});
