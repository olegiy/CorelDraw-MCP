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
  expect((saved!.profile as { vision: boolean }).vision).toBe(false);
  expect((saved!.profile as { send_preview: boolean }).send_preview).toBe(false);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).not.toBeVisible();
  await page.getByRole('button', { name: 'Настройки модели' }).click();
  await expect(page.getByLabel('API key', { exact: true })).toHaveValue('');
});

test('Vision and preview transmission need separate explicit choices', async ({ page }) => {
  let saved: { profile: { vision: boolean; send_preview: boolean } } | null = null;
  await page.route('**/api/v1/**', route => {
    if (route.request().method() === 'PUT') { saved = route.request().postDataJSON(); return route.fulfill({ json: saved!.profile }); }
    return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43), items: [], stale: true } });
  });
  await page.goto('/');
  await page.getByRole('button', { name: 'Настройки модели' }).click();
  await page.getByLabel('Модель', { exact: true }).fill('vision-test');
  await expect(page.getByLabel('Модель поддерживает Vision')).not.toBeChecked();
  await expect(page.getByLabel('Разрешить отправку PNG-превью модели')).toBeDisabled();
  await page.getByLabel('Модель поддерживает Vision').check();
  await expect(page.getByLabel('Разрешить отправку PNG-превью модели')).not.toBeChecked();
  await page.getByLabel('Разрешить отправку PNG-превью модели').check();
  await page.getByRole('button', { name: 'Сохранить в Credential Manager' }).click();
  await expect.poll(() => saved?.profile.send_preview).toBe(true);
  expect(saved!.profile.vision).toBe(true);
  await page.goto('about:blank');
});
