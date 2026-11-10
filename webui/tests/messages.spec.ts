import { expect, test, type Page } from '@playwright/test';

async function mockMessages(page: Page, messages: { id: string; role: 'user' | 'assistant' | 'tool'; text: string }[]) {
  const id = '00000000-0000-0000-0000-000000000001';
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: [{ id, conversation_id: id, kind: 'agent', status: 'running', last_seq: 0 }] } });
    if (path.endsWith('/messages')) return route.fulfill({ json: { items: messages.map(message => ({ ...message, at: '2026-11-05T00:00:00Z', run_id: id })) } });
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [], stale: true } });
  });
}

test('history renders plain text safely and registered download links without remote images', async ({ page }) => {
  let remoteRequests = 0;
  await page.route('https://attacker.test/**', route => { remoteRequests++; return route.abort(); });
  await mockMessages(page, [
    { id: 'text', role: 'assistant', text: '**Важный ответ**\n\n<script>window.leaked=true</script>\n\n![remote](https://attacker.test/private.png)\n\n[Внешняя ссылка](https://attacker.test/private)\n\n[Опасная ссылка](javascript:alert(1))\n\n<img src="https://attacker.test/raw.png" onerror="window.leaked=true">' },
    { id: 'artifact', role: 'tool', text: JSON.stringify({ artifact_id: 'result/1.svg?draft=1', display_name: 'result.svg', media_type: 'image/svg+xml', bytes: 100 }) },
  ]);
  await page.goto('/');
  await expect(page.getByRole('link', { name: 'Скачать result.svg' })).toHaveAttribute('href', '/api/v1/artifacts/result%2F1.svg%3Fdraft%3D1');
  await expect(page.locator('.messages a')).toHaveCount(1);
  await expect(page.locator('.messages img')).toHaveCount(0);
  await expect(page.locator('.messages script')).toHaveCount(0);
  await expect(page.locator('.messages span').filter({ hasText: 'Внешняя ссылка' })).toBeVisible();
  await expect(page.locator('.messages span').filter({ hasText: 'Опасная ссылка' })).toBeVisible();
  await expect(page.locator('.messages strong').filter({ hasText: 'Важный ответ' })).toBeVisible();
  expect(await page.evaluate(() => (window as Window & { leaked?: boolean }).leaked)).toBeUndefined();
  expect(remoteRequests).toBe(0);
  await page.goto('about:blank');
});

for (const width of [390, 1280]) {
  test(`message roles stay semantic, readable and contained with Markdown and long artifacts at ${width}px`, async ({ page }) => {
    const artifactName = `результат-${'а'.repeat(180)}.svg`;
    await mockMessages(page, [
      { id: 'user', role: 'user', text: 'Покажи рисунок' },
      { id: 'assistant', role: 'assistant', text: `Ответ\n\n${'д'.repeat(240)}\n\n- Первый шаг\n- Второй шаг\n\n\`\`\`vba\nDim name As String\n\`\`\`\n\n| Один | Два | Три | Четыре | Пять | Шесть | Семь | Восемь |\n| --- | --- | --- | --- | --- | --- | --- | --- |\n| 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |` },
      { id: 'tool', role: 'tool', text: JSON.stringify({ artifact_id: '00000000-0000-0000-0000-000000000002', display_name: artifactName }) },
    ]);
    await page.setViewportSize({ width, height: 844 });
    await page.goto('/');
    const messages = page.locator('.chat-surface .messages');
    await expect(messages.getByRole('article')).toHaveCount(3);
    for (const role of ['user', 'assistant', 'tool']) {
      const article = messages.locator(`article[data-role="${role}"]`);
      await expect(article).toHaveClass(new RegExp(`\\bmessage-${role}\\b`));
      await expect(article.locator('strong').first()).toHaveText(role);
      expect(await article.evaluate(element => element.getBoundingClientRect().width)).toBeLessThanOrEqual(680);
    }
    await expect(messages.locator('pre code')).toHaveText('Dim name As String\n');
    await expect(messages.getByRole('listitem')).toHaveCount(2);
    await expect(messages.getByRole('columnheader')).toHaveCount(8);
    await expect(messages.getByRole('link', { name: `Скачать ${artifactName}`, exact: true })).toBeVisible();
    expect(await messages.evaluate(element => parseFloat(getComputedStyle(element).rowGap))).toBeGreaterThanOrEqual(16);
    const user = messages.locator('article[data-role="user"]');
    const assistant = messages.locator('article[data-role="assistant"]');
    expect(await user.evaluate(element => getComputedStyle(element).backgroundColor)).not.toBe(await assistant.evaluate(element => getComputedStyle(element).backgroundColor));
    if (width === 1280) expect(await user.evaluate(element => element.getBoundingClientRect().left)).toBeGreaterThan(await assistant.evaluate(element => element.getBoundingClientRect().left));
    expect(await messages.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.goto('about:blank');
  });
}
