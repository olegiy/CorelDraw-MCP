import { expect, test, type Locator, type Page } from '@playwright/test';

async function mockWorkspace(page: Page, runs: unknown[] = []) {
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/health')) return route.fulfill({ json: { mcp_connected: true, store_ready: true, safety_barrier: null, readiness_reason: null } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { items: [], stale: false, at: null } });
    if (path.endsWith('/workspace')) return route.fulfill({ json: { output_directory: null } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: runs, next_offset: null } });
    return route.fulfill({ json: { items: [], next_offset: null } });
  });
}

function contrast(foreground: string, background: string) {
  const luminance = (color: string) => {
    const channels = color.match(/[\d.]+/g)!.slice(0, 3).map(value => {
      const channel = Number(value) / 255;
      return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
    });
    return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
  };
  const values = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
  return (values[0] + .05) / (values[1] + .05);
}

async function colors(locator: Locator) {
  return locator.evaluate(element => {
    const style = getComputedStyle(element);
    let surface: Element | null = element;
    while (surface && getComputedStyle(surface).backgroundColor === 'rgba(0, 0, 0, 0)') surface = surface.parentElement;
    let adjacent: Element | null = element.parentElement;
    while (adjacent && getComputedStyle(adjacent).backgroundColor === 'rgba(0, 0, 0, 0)') adjacent = adjacent.parentElement;
    return { text: style.color, background: getComputedStyle(surface!).backgroundColor, adjacent: getComputedStyle(adjacent!).backgroundColor, border: style.borderTopColor, outline: style.outlineColor };
  });
}

async function expectFocusRing(locator: Locator) {
  await expect(locator).toBeFocused();
  const ring = await locator.evaluate(element => {
    const style = getComputedStyle(element);
    return { visible: element.matches(':focus-visible'), width: parseFloat(style.outlineWidth), offset: parseFloat(style.outlineOffset), style: style.outlineStyle };
  });
  expect(ring.visible).toBe(true);
  expect(ring.width).toBeGreaterThanOrEqual(2);
  expect(ring.offset).toBeGreaterThanOrEqual(2);
  expect(ring.style).toBe('solid');
  const palette = await colors(locator);
  expect(contrast(palette.outline, palette.adjacent)).toBeGreaterThanOrEqual(3);
}

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

for (const width of [390, 1280]) {
  test(`stage 5 Tab traverses sidebar context and composer with visible focus at ${width}px`, async ({ page }) => {
    const id = '00000000-0000-0000-0000-000000000082';
    await mockWorkspace(page, [{ id, conversation_id: id, kind: 'agent', status: 'completed', last_seq: 0 }]);
    await page.setViewportSize({ width, height: 844 });
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Новая переписка', exact: true })).toBeVisible();
    await page.keyboard.press('Tab');
    await expectFocusRing(page.getByRole('button', { name: 'Новая переписка', exact: true }));
    await page.keyboard.press('Tab');
    const conversation = page.locator('.conversation-list').getByRole('button', { name: 'agent · completed', exact: true });
    await expectFocusRing(conversation);
    await page.keyboard.press('Enter');
    await expect(conversation).toHaveAttribute('aria-current', 'page');
    await expect(page.locator('.context-bar').getByRole('status')).toContainText('completed');
    await page.keyboard.press('Tab');
    const settings = page.getByRole('button', { name: 'Настройки модели', exact: true });
    await expectFocusRing(settings);
    await page.keyboard.press('Enter');
    const dialog = page.getByRole('dialog', { name: 'Профиль модели', exact: true });
    await expect(dialog).toBeVisible();
    expect(await dialog.evaluate(element => element.matches(':modal'))).toBe(true);
    await expectFocusRing(dialog.getByLabel('Модель', { exact: true }));
    await page.keyboard.press('Tab');
    await expectFocusRing(dialog.getByLabel('API URL', { exact: true }));
    await page.keyboard.press('Tab');
    await expectFocusRing(dialog.getByLabel('API key', { exact: true }));
    await page.keyboard.press('Tab');
    await expectFocusRing(dialog.getByRole('button', { name: 'Отмена', exact: true }));
    await page.keyboard.press('Tab');
    await expectFocusRing(dialog.getByRole('button', { name: 'Сохранить в Credential Manager', exact: true }));
    await page.keyboard.press('Shift+Tab');
    await expectFocusRing(dialog.getByRole('button', { name: 'Отмена', exact: true }));
    await page.keyboard.press('Escape');
    await expect(dialog).not.toBeVisible();
    await expect(settings).toBeFocused();
    await page.keyboard.press('Tab');
    const document = page.locator('.context-controls .document-disclosure');
    await expectFocusRing(document.locator(':scope > summary'));
    await page.keyboard.press('Enter');
    await expect(document.getByLabel('Целевой документ', { exact: true })).toBeVisible();
    await page.keyboard.press('Space');
    await expect(document.getByLabel('Целевой документ', { exact: true })).not.toBeVisible();
    await page.keyboard.press('Tab');
    const files = page.locator('.context-controls .file-disclosure');
    await expectFocusRing(files.locator(':scope > summary'));
    await page.keyboard.press('Space');
    await expect(files.getByLabel('Открыть локальный CDR', { exact: true })).toBeVisible();
    await page.keyboard.press('Enter');
    await expect(files.getByLabel('Открыть локальный CDR', { exact: true })).not.toBeVisible();
    await page.keyboard.press('Tab');
    await expectFocusRing(page.locator('.technical-disclosure > summary'));
    await page.keyboard.press('Tab');
    const checkbox = page.getByRole('checkbox', { name: 'Агент создаёт новый документ', exact: true });
    await expectFocusRing(checkbox);
    await page.keyboard.press('Space');
    await expect(checkbox).toBeChecked();
    await page.keyboard.press('Tab');
    const composer = page.getByLabel('Сообщение агенту', { exact: true });
    await expectFocusRing(composer);
    await page.keyboard.type('Проверка доступности');
    await page.keyboard.press('Tab');
    await expectFocusRing(page.getByRole('button', { name: 'Отправить', exact: true }));
  });
}

test('stage 5 computed text and enabled control colors meet contrast thresholds including hover and danger', async ({ page }) => {
  await mockWorkspace(page);
  await page.goto('/');
  await page.locator('.context-controls .document-disclosure > summary').click();
  await page.locator('.context-controls .file-disclosure > summary').click();
  await page.locator('.technical-disclosure > summary').click();
  await page.getByLabel('Сообщение агенту', { exact: true }).fill('Проверка контраста');
  const text = page.locator('h1, h2, .sidebar-brand, .sidebar-brand small, .context-bar p, .selected-model, .chat-surface small, .muted, label, summary, .technical-content p');
  for (const locator of await text.all()) {
    if (!await locator.isVisible()) continue;
    const palette = await colors(locator);
    expect(contrast(palette.text, palette.background), await locator.textContent() ?? '').toBeGreaterThanOrEqual(4.5);
  }
  const controls = page.locator('button:enabled, input:not([type="checkbox"]):enabled, select:enabled, textarea:enabled');
  for (const locator of await controls.all()) {
    if (!await locator.isVisible()) continue;
    const palette = await colors(locator);
    expect(contrast(palette.text, palette.background)).toBeGreaterThanOrEqual(4.5);
    if (await locator.evaluate(element => element.matches('input, select'))) expect(contrast(palette.border, palette.background)).toBeGreaterThanOrEqual(3);
  }
  for (const locator of [page.getByRole('button', { name: 'Новая переписка', exact: true }), page.getByRole('button', { name: 'Отправить', exact: true })]) {
    const normal = await colors(locator);
    await locator.hover();
    const hovered = await colors(locator);
    expect(hovered.background).not.toBe(normal.background);
    expect(contrast(hovered.text, hovered.background)).toBeGreaterThanOrEqual(4.5);
  }
  await page.route('**/api/v1/documents', route => route.fulfill({ status: 500, json: { message: 'Ошибка тестового документа' } }));
  await page.reload();
  const error = page.locator('.error').filter({ hasText: 'Ошибка тестового документа' });
  await expect(error).toBeVisible();
  const palette = await colors(error);
  expect(contrast(palette.text, palette.background)).toBeGreaterThanOrEqual(4.5);
  await page.getByRole('button', { name: 'Настройки модели', exact: true }).click();
  for (const locator of await page.getByRole('dialog').locator('h2, p, label, input, button').all()) {
    const palette = await colors(locator);
    expect(contrast(palette.text, palette.background)).toBeGreaterThanOrEqual(4.5);
    if (await locator.evaluate(element => element.matches('input'))) expect(contrast(palette.border, palette.background)).toBeGreaterThanOrEqual(3);
  }
  await page.keyboard.press('Escape');
});

test('stage 5 disabled submit remains distinguishable and ignores hover without hiding its label', async ({ page }) => {
  await mockWorkspace(page);
  await page.goto('/');
  const submit = page.getByRole('button', { name: 'Отправить', exact: true });
  await expect(submit).toBeDisabled();
  const disabled = await submit.evaluate(element => ({ opacity: Number(getComputedStyle(element).opacity), cursor: getComputedStyle(element).cursor, background: getComputedStyle(element).backgroundColor }));
  expect(disabled.opacity).toBeGreaterThan(0);
  expect(disabled.opacity).toBeLessThan(1);
  expect(disabled.cursor).toBe('default');
  await submit.hover();
  expect((await colors(submit)).background).toBe(disabled.background);
  await page.getByLabel('Сообщение агенту', { exact: true }).fill('Текст');
  await expect(submit).toBeEnabled();
  expect(await submit.evaluate(element => Number(getComputedStyle(element).opacity))).toBe(1);
});

test('stage 5 active Stop has readable danger and hover states and a contrasting keyboard ring', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000081';
  const active = { id, conversation_id: id, kind: 'manual', status: 'running', last_seq: 0 };
  await mockWorkspace(page);
  await page.route('**/api/v1/runs?*', route => route.fulfill({ json: { items: [active], next_offset: null } }));
  await page.route(`**/api/v1/runs/${id}`, route => route.fulfill({ json: active }));
  await page.route('**/api/v1/runs/*/events?*', route => route.fulfill({ contentType: 'text/event-stream', body: '' }));
  await page.goto('/');
  const stop = page.getByRole('button', { name: 'Стоп', exact: true });
  await expect(stop).toBeEnabled();
  const normal = await colors(stop);
  expect(contrast(normal.text, normal.background)).toBeGreaterThanOrEqual(4.5);
  expect(contrast(normal.border, normal.adjacent)).toBeGreaterThanOrEqual(3);
  await stop.hover();
  const hovered = await colors(stop);
  expect(hovered.background).not.toBe(normal.background);
  expect(contrast(hovered.text, hovered.background)).toBeGreaterThanOrEqual(4.5);
  expect(contrast(hovered.border, hovered.adjacent)).toBeGreaterThanOrEqual(3);
  await page.getByRole('button', { name: 'Настройки модели', exact: true }).focus();
  await page.keyboard.press('Tab');
  await expectFocusRing(stop);
  await page.goto('about:blank');
});

test('stage 5 reduced motion removes nonessential animation and transition even on a themed control', async ({ page }) => {
  await mockWorkspace(page);
  await page.goto('/');
  await page.addStyleTag({ content: '@keyframes test-motion { from { opacity: .8; } to { opacity: 1; } } .composer-submit { animation: test-motion 2s infinite; transition: background-color .2s; scroll-behavior: smooth; }' });
  const submit = page.getByRole('button', { name: 'Отправить', exact: true });
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  expect(await submit.evaluate(element => getComputedStyle(element).animationName)).toBe('test-motion');
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const motion = await submit.evaluate(element => {
    const style = getComputedStyle(element);
    return { animation: style.animationName, transition: style.transitionProperty, scroll: style.scrollBehavior };
  });
  expect(motion).toEqual({ animation: 'none', transition: 'none', scroll: 'auto' });
});

test('stage 5 native surfaces keep one main heading and named icon-only controls if present', async ({ page }) => {
  await mockWorkspace(page);
  await page.goto('/');
  await page.locator('.context-controls .document-disclosure > summary').click();
  await page.locator('.context-controls .file-disclosure > summary').click();
  await expect(page.locator('h1')).toHaveCount(1);
  await expect(page.locator('main h1')).toHaveText('CorelDRAW Agent');
  expect(await page.locator('main h1, main h2, main h3, main h4, main h5, main h6').evaluateAll(elements => elements.map(element => Number(element.tagName.slice(1))))).toEqual([1, 2, 2]);
  await page.getByRole('button', { name: 'Настройки модели', exact: true }).click();
  await expect(page.getByRole('dialog').getByRole('heading', { level: 2, name: 'Профиль модели', exact: true })).toBeVisible();
  for (const button of await page.locator('button').all()) {
    if ((await button.textContent())?.trim()) continue;
    await expect(button).toHaveAttribute('aria-label', /\S/);
    await expect(button).toHaveAttribute('title', /\S/);
  }
  await page.keyboard.press('Escape');
});
