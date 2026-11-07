import assert from 'node:assert/strict';
import { createInterface } from 'node:readline';
import { chromium, expect } from '../../webui/node_modules/@playwright/test/index.mjs';

const browser = await chromium.launch({ channel: 'msedge', headless: true });
let context = await browser.newContext();
let page;
const errors = [];
const origin = 'http://127.0.0.1:8878';
async function api(method, path, body) {
  const session = await context.request.get(`${origin}/api/v1/access/session`, { headers: { Connection: 'close' } });
  const csrf = (await session.json()).csrf_token;
  const response = await context.request.fetch(`${origin}/api/v1${path}`, { method, maxRedirects: 0,
    headers: { 'X-Corel-CSRF': csrf, Origin: origin, Connection: 'close' }, ...(body ? { data: body } : {}) });
  return { status: response.status(), body: await response.json() };
}
try {
  for await (const line of createInterface({ input: process.stdin })) {
    const command = JSON.parse(line);
    try {
      let value;
      if (command.op === 'open') {
        if (command.reset) { await context.close(); context = await browser.newContext(); }
        page = await context.newPage(); page.setDefaultTimeout(15000);
        page.on('pageerror', error => errors.push(error.name));
        assert.equal(new URL(command.url).origin, origin);
        await page.goto(command.url);
        await expect.poll(async () => (await api('GET', '/health')).status).toBe(200);
        assert.equal(new URL(page.url()).hash, '');
        const cookies = await context.cookies();
        assert.equal(cookies.length, 1); assert.equal(cookies[0].httpOnly, true);
        assert.equal(cookies[0].sameSite, 'Strict');
        assert.deepEqual(await page.evaluate(() => [localStorage.length, sessionStorage.length]), [0, 0]);
        await expect(page.getByRole('heading', { name: 'CorelDRAW Agent', exact: true })).toBeVisible();
        value = { bootstrap_cleared: true, private_cookie: true };
      } else if (command.op === 'api') {
        value = await api(command.method, command.path, command.body);
      } else if (command.op === 'agent') {
        await page.reload();
        await page.getByLabel('Только чтение', { exact: true }).uncheck();
        await page.getByLabel('Целевой документ').selectOption(command.document_id);
        await page.getByLabel('Сообщение агенту').fill(command.message);
        const accepted = page.waitForResponse(response => response.url().endsWith('/api/v1/runs') && response.request().method() === 'POST');
        await page.getByRole('button', { name: 'Отправить', exact: true }).click();
        const response = await accepted; assert.equal(response.status(), 202);
        value = await response.json();
      } else if (command.op === 'approve') {
        const pending = command.pending;
        await expect(page.getByRole('dialog')).toBeVisible();
        assert.equal(pending.operation.arguments.code.replaceAll('\r\n', '\n').trim(), command.code.trim());
        const first = page.waitForResponse(response => response.url().endsWith(`/runs/${command.run_id}/confirm`));
        await page.getByRole('button', { name: 'Подтвердить один раз', exact: true }).click();
        const second = await api('POST', `/runs/${command.run_id}/confirm`, {
          confirmation_id: pending.confirmation_id, operation_digest: pending.operation_digest, approved: true });
        assert.equal((await first).status(), 200); assert.ok([200, 409].includes(second.status));
        value = { second_status: second.status };
      } else if (command.op === 'close-tabs') {
        for (const open of context.pages()) { await open.goto('about:blank'); await open.close(); }
        value = { tabs: context.pages().length };
      } else if (command.op === 'history') {
        await page.reload();
        await page.getByRole('button', { name: command.label, exact: true }).first().click();
        await expect(page.getByRole('status')).toContainText(command.status);
        await page.setViewportSize({ width: 390, height: 844 });
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
        if (command.screenshot) await page.screenshot({ path: command.screenshot });
        value = { restored: true, errors, mobile_no_overflow: true };
      } else if (command.op === 'exit') {
        console.log(JSON.stringify({ ok: true, value: { errors } })); break;
      } else { throw new Error('Unknown coordinator command'); }
      console.log(JSON.stringify({ ok: true, value }));
    } catch (error) {
      console.log(JSON.stringify({ ok: false, error: error.name }));
    }
  }
} finally {
  await context.close(); await browser.close();
}
