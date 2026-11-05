import { expect, test } from '@playwright/test';

test('bootstrap fragment is cleared before header exchange and never stored', async ({ page }) => {
  const token = 'a'.repeat(43);
  let exchanged = false;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/bootstrap')) {
      expect(new URL(page.url()).hash).toBe('');
      expect(route.request().headers()['x-corel-bootstrap']).toBe(token);
      expect(route.request().url()).not.toContain(token);
      exchanged = true;
      await route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    } else if (path.endsWith('/access/session')) {
      await route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    } else if (path.endsWith('/documents')) {
      await route.fulfill({ json: { items: [], stale: true, at: null } });
    } else {
      await route.fulfill({ json: { items: [] } });
    }
  });
  await page.goto(`/#bootstrap=${token}`);
  await expect.poll(() => exchanged).toBe(true);
  expect(await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }))).not.toContain(token);
});

test('narrow viewport remains keyboard accessible without horizontal overflow', async ({ page }) => {
  await page.route('**/api/v1/**', route => route.fulfill({ json: { authenticated: true,
    csrf_token: 'b'.repeat(43), items: [], stale: true, at: null } }));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'CorelDRAW Agent' })).toBeVisible();
  await page.keyboard.press('Tab');
  expect(await page.evaluate(() => document.activeElement?.tagName)).toBe('BUTTON');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
