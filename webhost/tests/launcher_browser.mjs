// Test-owned browser only. The server is the actual fixed-port launcher;
// its external Corel executor is replaced by the Python acceptance fixture.
import assert from 'node:assert/strict';
import { chromium, expect } from '../../webui/node_modules/@playwright/test/index.mjs';

const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const context = await browser.newContext();
  const page = await context.newPage();
  page.setDefaultTimeout(10000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.name));
  const origin = 'http://127.0.0.1:8878';
  assert.equal(new URL(process.env.COREL_TEST_BOOTSTRAP_URL).origin, origin);
  await page.goto(process.env.COREL_TEST_BOOTSTRAP_URL);
  await expect(page.getByRole('heading', { name: 'CorelDRAW Agent', exact: true })).toBeVisible();
  await expect.poll(async () => (await context.request.get(`${origin}/api/v1/access/session`,
    { headers: { Connection: 'close' } })).status()).toBe(200);
  assert.equal(new URL(page.url()).hash, '');
  await expect(page.getByLabel('Только чтение', { exact: true })).toBeChecked();
  assert.deepEqual(await page.evaluate(() => [localStorage.length, sessionStorage.length]), [0, 0]);
  const cookies = await context.cookies();
  assert.equal(cookies.length, 1);
  assert.equal(cookies[0].name, 'corel_agent_session');
  assert.equal(cookies[0].httpOnly, true);
  assert.equal(cookies[0].sameSite, 'Strict');
  await expect(page.getByRole('alert')).toHaveCount(0);

  await page.getByRole('button', { name: 'Обновить документы', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('completed');
  await expect(page.getByLabel('Целевой документ')).toBeEnabled();
  await page.reload();
  await page.getByRole('button', { name: 'manual · completed', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('completed');
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  assert.deepEqual(errors, []);
  await page.goto('about:blank');
  await context.close();
  console.log(JSON.stringify({ bootstrap_cleared: true, history_restored: true, mobile_no_overflow: true }));
} finally {
  await browser.close();
}
