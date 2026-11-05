import { expect, test } from '@playwright/test';

test('real host keeps accepted run after tab closes and rejects anonymous access', async ({ browser }) => {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto('http://127.0.0.1:18880/');
  await page.getByRole('button', { name: 'Обновить документы' }).click();
  await expect(page.getByRole('status')).toContainText('completed');
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await page.getByLabel('Сообщение агенту').fill('Получить превью');
  await page.getByRole('button', { name: 'Отправить', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('running');
  await page.close();
  const reopened = await context.newPage();
  await reopened.goto('http://127.0.0.1:18880/');
  await expect.poll(async () => {
    const response = await context.request.get('http://127.0.0.1:18880/api/v1/runs');
    const body = await response.json();
    return body.items.find((run: { kind: string }) => run.kind === 'agent')?.status;
  }).toBe('completed');
  const snapshot = await context.request.get('http://127.0.0.1:18880/api/v1/runs');
  const runs = (await snapshot.json()).items;
  expect(runs.filter((run: { kind: string }) => run.kind === 'agent')).toHaveLength(1);
  const anonymous = await browser.newContext();
  const denied = await anonymous.request.get('http://127.0.0.1:18880/api/v1/health');
  expect(denied.status()).toBe(401);
  await anonymous.close();
  await context.close();
});

test('two tabs observe one accepted run and explicit Stop prevents next native step', async ({ browser }) => {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto('http://127.0.0.1:18880/');
  await page.getByRole('button', { name: 'Обновить документы' }).click();
  await expect(page.getByRole('status')).toContainText('completed');
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await page.getByLabel('Сообщение агенту').fill('Превью, затем закончить');
  await page.getByRole('button', { name: 'Отправить', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('running');
  const second = await context.newPage();
  await second.goto('http://127.0.0.1:18880/');
  await expect(second.getByRole('status')).toContainText('running');
  await second.getByRole('button', { name: 'Стоп', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('stopped');
  await expect(second.getByRole('status')).toContainText('stopped');
  const response = await context.request.get('http://127.0.0.1:18880/api/v1/runs');
  const stopped = (await response.json()).items.find((run: { status: string }) => run.status === 'stopped');
  expect(stopped.stop_requested).toBe(true);
  expect(stopped.counters.calls).toBeLessThanOrEqual(1);
  await context.close();
});
