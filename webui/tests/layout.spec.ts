import { expect, test, type Page } from '@playwright/test';

async function mockApi(page: Page, runs: unknown[] = []) {
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/health')) return route.fulfill({ json: { mcp_connected: true, store_ready: true, safety_barrier: null, readiness_reason: null } });
    if (path.endsWith('/runs')) return route.fulfill({ json: { items: runs, next_offset: null } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { items: [], stale: true, at: null } });
    if (path.endsWith('/profiles')) return route.fulfill({ json: { items: [] } });
    if (path.endsWith('/workspace')) return route.fulfill({ json: { output_directory: null } });
    return route.fulfill({ json: { items: [] } });
  });
}

test('does not display the duplicate top header while retaining document controls', async ({ page }) => {
  await mockApi(page);
  await page.goto('/');
  await expect(page.locator('.context-bar')).not.toBeVisible();
  await expect(page.locator('.sidebar-brand')).toContainText('CorelDRAW Agent');
  await expect(page.locator('.document-disclosure > summary')).toBeVisible();
});

test('stage 5 named design tokens resolve to usable colors spacing and radii on the real shell', async ({ page }) => {
  await mockApi(page);
  await page.goto('/');
  const tokens = await page.locator('.app-shell').evaluate(element => {
    const style = getComputedStyle(element);
    return Object.fromEntries(['--color-canvas', '--color-surface', '--color-text', '--color-muted', '--color-border', '--color-accent', '--color-danger', '--space-1', '--space-2', '--space-3', '--space-4', '--space-5', '--space-6', '--radius-sm', '--radius-md'].map(name => [name, style.getPropertyValue(name).trim()]));
  });
  for (const [name, value] of Object.entries(tokens)) {
    expect(value, name).not.toBe('');
    if (name.startsWith('--color')) expect(await page.evaluate(color => CSS.supports('color', color), value), name).toBe(true);
    else expect(parseFloat(value), name).toBeGreaterThan(0);
  }
  const spaces = Array.from({ length: 6 }, (_, index) => parseFloat(tokens[`--space-${index + 1}`]));
  expect(spaces.every((space, index) => index === 0 || space > spaces[index - 1])).toBe(true);
  expect(parseFloat(tokens['--radius-md'])).toBeGreaterThanOrEqual(parseFloat(tokens['--radius-sm']));
  expect(await page.locator('body').evaluate(element => getComputedStyle(element).fontFamily)).toMatch(/system-ui/);
  expect(await page.evaluate(() => Array.from(document.querySelectorAll('link[rel="stylesheet"]')).every(element => new URL((element as HTMLLinkElement).href).origin === location.origin))).toBe(true);
  await page.locator('.app-shell').evaluate(element => {
    const style = (element as HTMLElement).style;
    style.setProperty('--color-text', 'rgb(20, 30, 40)');
    style.setProperty('--color-accent', 'rgb(30, 60, 120)');
    style.setProperty('--color-surface', 'rgb(250, 250, 250)');
    style.setProperty('--space-2', '9px');
    style.setProperty('--radius-sm', '7px');
  });
  await expect(page.locator('.chat-composer')).toHaveCSS('background-color', 'rgb(250, 250, 250)');
  await expect(page.getByRole('button', { name: 'Новая переписка', exact: true })).toHaveCSS('color', 'rgb(20, 30, 40)');
  await expect(page.getByRole('button', { name: 'Отправить', exact: true })).toHaveCSS('background-color', 'rgb(30, 60, 120)');
  await expect(page.getByRole('button', { name: 'Новая переписка', exact: true })).toHaveCSS('gap', '9px');
  await expect(page.getByRole('button', { name: 'Новая переписка', exact: true })).toHaveCSS('border-radius', '7px');
});

test('stage 5 long mobile values and focused fields fit scrollable context controls without clipping', async ({ page }) => {
  const documentName = 'Рисунок-' + 'длинноеимя'.repeat(60) + '.cdr';
  const model = 'model-' + 'm'.repeat(400);
  const path = 'C:\\drawings\\' + 'p'.repeat(800) + '.cdr';
  await mockApi(page);
  await page.route('**/api/v1/documents', route => route.fulfill({ json: { items: [{ document_id: 'fixture-long', name: documentName, path }], stale: false, at: null } }));
  await page.route('**/api/v1/profiles', route => route.fulfill({ json: { items: [{ id: '00000000-0000-0000-0000-000000000010', model, base_url: 'http://127.0.0.1:1234/v1/' + 'u'.repeat(800) }] } }));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await page.locator('.context-controls .document-disclosure > summary').click();
  await page.getByLabel('Целевой документ', { exact: true }).selectOption('fixture-long');
  await page.locator('.context-controls .file-disclosure > summary').click();
  await page.getByLabel('Открыть локальный CDR', { exact: true }).fill(path);
  await page.getByLabel('Имя результата', { exact: true }).fill('результат'.repeat(100) + '.cdr');
  await page.locator('.technical-disclosure > summary').click();
  for (const selector of ['.document-disclosure > summary', '[data-testid="provider-scope"]', '[data-testid="save-destination"]']) {
    const element = page.locator(selector);
    const bounds = await element.evaluate(element => ({ width: element.clientWidth, scroll: element.scrollWidth, left: element.getBoundingClientRect().left, right: element.getBoundingClientRect().right }));
    expect(bounds.scroll).toBeLessThanOrEqual(bounds.width);
    expect(bounds.left).toBeGreaterThanOrEqual(0);
    expect(bounds.right).toBeLessThanOrEqual(390);
  }
  await expect(page.locator('.document-disclosure > summary')).toContainText(documentName);
  await expect(page.getByTestId('selected-model')).toHaveAttribute('title', model);
  await expect(page.getByTestId('save-destination')).toContainText(path);
  const field = page.getByLabel('Имя результата', { exact: true });
  await field.focus();
  await page.keyboard.press('Shift+Tab');
  await page.keyboard.press('Tab');
  await expect(field).toBeFocused();
  const geometry = await field.evaluate(element => {
    const bounds = element.getBoundingClientRect();
    const parent = element.closest('.disclosure-content')!.getBoundingClientRect();
    const style = getComputedStyle(element);
    const ring = parseFloat(style.outlineWidth) + parseFloat(style.outlineOffset);
    return { left: bounds.left - ring, right: bounds.right + ring, parentLeft: parent.left, parentRight: parent.right, hit: document.elementFromPoint(bounds.left + bounds.width / 2, bounds.top + bounds.height / 2) === element };
  });
  expect(geometry.left).toBeGreaterThanOrEqual(geometry.parentLeft);
  expect(geometry.right).toBeLessThanOrEqual(geometry.parentRight);
  expect(geometry.hit).toBe(true);
  expect(await page.locator('.context-controls').evaluate(element => getComputedStyle(element).position)).toBe('static');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(await page.evaluate(() => [document.documentElement, document.body, document.querySelector('.app-main')!].every(element => !['hidden', 'clip'].includes(getComputedStyle(element).overflowX)))).toBe(true);
});

test('stage 4 empty screen keeps document and file controls collapsed at context, not after chat', async ({ page }) => {
  await mockApi(page);
  await page.goto('/');
  await expect(page.locator('.document-disclosure > summary')).toContainText('Документ не выбран');
  await expect(page.getByLabel('Целевой документ', { exact: true })).not.toBeVisible();
  await expect(page.getByLabel('Открыть локальный CDR', { exact: true })).not.toBeVisible();
  await expect(page.getByTestId('provider-scope')).not.toBeVisible();
  await expect(page.getByTestId('save-destination')).not.toBeVisible();
  await expect(page.getByTestId('job-stage')).not.toBeVisible();
  await expect(page.locator('.context-controls .document-disclosure > summary')).toBeVisible();
  await expect(page.locator('.context-controls .file-disclosure > summary')).toBeVisible();
  await expect(page.locator('.context-controls').getByText('Список устарел', { exact: true })).toBeVisible();
  expect(await page.locator('.context-controls').evaluate(element => element.previousElementSibling?.classList.contains('primary-alerts') && element.nextElementSibling?.classList.contains('chat-surface'))).toBe(true);
  await expect(page.locator('.activity-disclosure')).toHaveCount(0);
  await expect(page.getByTestId('global-barrier')).toHaveCount(0);
  await expect(page.locator('.chat-surface').getByText('Закрытие вкладки не останавливает отправленную операцию.', { exact: false })).toBeVisible();
  expect(await page.getByLabel('Сообщение агенту', { exact: true }).evaluate(element => element.getBoundingClientRect().bottom <= innerHeight)).toBe(true);
});

test('stage 4 compact model and keyboard technical disclosure retain exact provider save and frozen Run context', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000031';
  let posts = 0;
  await mockApi(page, [{ id, conversation_id: id, kind: 'agent', status: 'completed', last_seq: 0, document_id: 'fixture-b', job_id: 'job-frozen', monitoring_state: 'healthy', profile: { model: 'frozen-model', base_url: 'https://frozen.example/v1' } }]);
  await page.route('**/api/v1/profiles', route => route.fulfill({ json: { items: [{ id, model: 'next-model', base_url: 'https://next.example/v1' }] } }));
  await page.route('**/api/v1/documents', route => route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B', path: 'C:\\drawings\\selected.cdr' }], stale: false } }));
  await page.route('**/api/v1/**', route => { if (route.request().method() === 'POST') posts++; return route.fallback(); });
  await page.goto('/');
  await page.locator('.conversation-list').getByRole('button', { name: 'agent · completed', exact: true }).click();
  await expect(page.locator('.context-controls').getByTestId('selected-model')).toHaveText('Модель: next-model');
  const technical = page.locator('.chat-surface .technical-disclosure');
  const trigger = technical.locator(':scope > summary');
  await expect(technical.getByTestId('provider-scope')).not.toBeVisible();
  await trigger.focus();
  await page.keyboard.press('Enter');
  await expect(technical.getByTestId('provider-scope')).toHaveText('Следующий запрос: next-model · https://next.example/v1 · Контекст и проверенные PNG-превью передаются автоматически');
  await expect(technical.getByTestId('save-destination')).toHaveText('Save выбранного CDR: C:\\drawings\\selected.cdr');
  await expect(technical.getByText('Зафиксированный профиль Run:', { exact: false })).toHaveText('Зафиксированный профиль Run: frozen-model · https://frozen.example/v1');
  await expect(technical.getByText('Цель Run:', { exact: false })).toHaveText('Цель Run: fixture-b · Job: job-frozen · Мониторинг: healthy');
  await expect(technical.getByTestId('job-stage')).toBeVisible();
  await page.keyboard.press('Space');
  await expect(technical.getByTestId('save-destination')).not.toBeVisible();
  await page.keyboard.press('Enter');
  await expect(technical.getByTestId('save-destination')).toBeVisible();
  await expect(technical.getByText('Зафиксированный профиль Run:', { exact: false })).toContainText('frozen-model');
  expect(posts).toBe(0);
});

for (const width of [390, 1280]) {
  test(`stage 4 document disclosure is keyboard reachable and retains selection at ${width}px`, async ({ page }) => {
    let posts = 0;
    await mockApi(page);
    await page.route('**/api/v1/documents', route => route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B', path: 'C:\\drawings\\selected.cdr', active_page: 2 }], stale: false, at: '2026-11-09T00:00:00Z' } }));
    await page.route('**/api/v1/**', route => { if (route.request().method() === 'POST') posts++; return route.fallback(); });
    await page.setViewportSize({ width, height: 844 });
    await page.goto('/');
    const disclosure = page.locator('.context-controls .document-disclosure');
    const trigger = disclosure.locator(':scope > summary');
    await expect(trigger).toHaveAccessibleName('Документ не выбран · Действия документа');
    const settings = page.locator('.sidebar-footer').getByRole('button', { name: 'Настройки модели', exact: true });
    await settings.focus();
    await page.keyboard.press('Tab');
    await expect(trigger).toBeFocused();
    await page.keyboard.press('Enter');
    const select = disclosure.getByLabel('Целевой документ', { exact: true });
    await expect(select).toBeVisible();
    await page.keyboard.press('Tab');
    await expect(disclosure.getByRole('button', { name: 'Обновить документы', exact: true })).toBeFocused();
    await select.selectOption('fixture-b');
    await expect(trigger).toContainText('Рисунок B');
    await expect(disclosure.getByText('Кэш: 2026-11-09T00:00:00Z', { exact: true })).toBeVisible();
    for (const name of ['Инспекция', 'Сохранить выбранный CDR', 'Закрыть документ', 'Превью страницы', 'Превью выделения']) {
      await expect(disclosure.getByRole('button', { name, exact: true })).toBeVisible();
      await expect(disclosure.getByRole('button', { name, exact: true })).toBeEnabled();
    }
    await trigger.focus();
    await page.keyboard.press('Space');
    await expect(select).not.toBeVisible();
    await expect(trigger).toContainText('Рисунок B');
    await page.keyboard.press('Enter');
    await expect(select).toHaveValue('fixture-b');
    expect(posts).toBe(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  });
}

test('stage 4 activity summary hides every operation until disclosure and preserves reports on reopen', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000021';
  await mockApi(page, [{ id, conversation_id: id, kind: 'manual', status: 'completed', last_seq: 1 }]);
  await page.route('**/api/v1/runs/*/operations?*', route => route.fulfill({ json: { items: [
    { id: 'op/one', run_id: id, state: 'terminal', job_id: 'job-exact', prepared: { tool: 'corel_export', arguments: { path: 'C:\\exports\\exact.svg', format: 'svg', range: 'selection' } }, result: { outcome: 'succeeded', applied: true, cleanup: { completed: true }, report: { artifact_id: 'report/one', display_name: 'export.json' } } },
    { id: 'op-two', run_id: id, state: 'prepared', prepared: { tool: 'corel_inspect_document', arguments: {} } },
  ], next_offset: null } }));
  await page.goto('/');
  await page.locator('.conversation-list').getByRole('button', { name: 'manual · completed', exact: true }).click();
  const activity = page.locator('.activity-disclosure');
  const trigger = activity.locator(':scope > summary');
  await expect(trigger).toHaveText('Активность · 2 операции');
  await expect(activity.locator('.operation-card > summary')).toHaveCount(2);
  for (const summary of await activity.locator('.operation-card > summary').all()) await expect(summary).not.toBeVisible();
  await trigger.focus();
  await page.keyboard.press('Enter');
  const operation = activity.locator('.operation-card').filter({ hasText: 'corel_export' });
  await expect(operation.locator(':scope > summary')).toHaveText('corel_export · succeeded');
  await expect(operation.getByText('Job: job-exact', { exact: true })).not.toBeVisible();
  await operation.locator(':scope > summary').focus();
  await page.keyboard.press('Space');
  await expect(operation.getByText('Job: job-exact', { exact: true })).toBeVisible();
  await expect(operation.getByText('Applied: true · Cleanup: true', { exact: true })).toBeVisible();
  await expect(operation.locator('pre')).toHaveText('{\n  "path": "C:\\\\exports\\\\exact.svg",\n  "format": "svg",\n  "range": "selection"\n}');
  await expect(operation.getByRole('link', { name: 'Скачать export.json', exact: true })).toHaveAttribute('href', '/api/v1/artifacts/report%2Fone');
  await expect(operation.getByRole('link', { name: 'Локальный отчёт', exact: true })).toHaveAttribute('href', '/api/v1/operations/op%2Fone/report');
  await trigger.click();
  await expect(operation.locator('pre')).not.toBeVisible();
  await trigger.click();
  await expect(operation.getByText('Job: job-exact', { exact: true })).toBeVisible();
  await expect(operation.locator('pre')).toContainText('selection');
  await page.getByRole('button', { name: 'Новая переписка', exact: true }).click();
  await expect(activity).toHaveCount(0);
});

test('renders the chat-first shell and preserves current accessible labels', async ({ page }) => {
  await mockApi(page);
  await page.goto('/');

  await expect(page.getByRole('heading', { name: 'CorelDRAW Agent', exact: true })).toHaveCount(1);
  await expect(page.locator('h1')).toHaveCount(1);
  await expect(page.getByRole('heading', { name: 'CorelDRAW Agent', exact: true })).toBeVisible();
  await expect(page.locator('.app-shell')).toBeVisible();
  await expect(page.locator('.app-sidebar')).toBeVisible();
  await expect(page.locator('.app-main')).toBeVisible();
  await expect(page.locator('.context-bar')).toHaveCount(0);
  await expect(page.locator('.chat-surface')).toBeVisible();
  await expect(page.locator('.app-sidebar').getByRole('heading', { name: 'CorelDRAW Agent', exact: true })).toBeVisible();
  await expect(page.locator('.sidebar-brand')).toContainText('CorelDRAW Agent');
  await expect(page.locator('.app-main').getByText('Локальная панель · операции выполняются без подтверждений; VBA имеет доступ к ОС', { exact: true })).toBeVisible();
  await expect(page.locator('.document-disclosure > summary')).toContainText('Документ не выбран');
  await expect(page.getByRole('button', { name: 'Стоп', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Новая переписка', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Настройки модели', exact: true })).toBeVisible();
  await expect(page.getByLabel('Сообщение агенту', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Отправить', exact: true })).toBeVisible();
});

test('shows recent runs as sidebar conversation affordances without empty technical cards', async ({ page }) => {
  await mockApi(page, [
    { id: '00000000-0000-0000-0000-000000000001', conversation_id: '00000000-0000-0000-0000-000000000002', kind: 'manual', status: 'completed', last_seq: 1 },
    { id: '00000000-0000-0000-0000-000000000003', conversation_id: '00000000-0000-0000-0000-000000000004', kind: 'agent', status: 'stopped', last_seq: 2 },
  ]);
  await page.goto('/');

  const conversations = page.locator('.conversation-list');
  await expect(conversations).toBeVisible();
  await expect(conversations.getByRole('button', { name: 'manual · completed', exact: true })).toBeVisible();
  await expect(conversations.getByRole('button', { name: 'agent · stopped', exact: true })).toBeVisible();
  await expect(page.locator('.app-main .panel').getByRole('heading', { name: 'История', exact: true })).toHaveCount(0);
  await expect(page.locator('.app-main .panel').getByRole('heading', { name: 'Файлы', exact: true })).toHaveCount(0);
  await expect(page.locator('.app-main .panel').getByRole('heading', { name: 'Операции', exact: true })).toHaveCount(0);
});

for (const width of [390, 720]) {
  test(`keeps the shell within a ${width}px viewport without horizontal overflow`, async ({ page }) => {
    await mockApi(page);
    await page.setViewportSize({ width, height: 844 });
    await page.goto('/');

    await expect(page.getByRole('heading', { name: 'CorelDRAW Agent', exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  });
}

test('history selection restores document and output context without sending a new request', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000005';
  const documentName = `Рисунок-${'длинное-имя'.repeat(12)}.cdr`;
  const outputDirectory = 'C:\\exports\\archive';
  let posts = 0;
  await mockApi(page, [{ id, conversation_id: '00000000-0000-0000-0000-000000000006', kind: 'manual', status: 'completed', last_seq: 1, document_id: 'fixture-history', output_directory: outputDirectory }]);
  await page.route('**/api/v1/documents', route => route.fulfill({ json: { items: [{ document_id: 'fixture-history', name: documentName, active_page: 1, path: 'C:\\drawings\\history.cdr' }], stale: false, at: null } }));
  await page.route('**/api/v1/**', route => {
    if (route.request().method() === 'POST') posts++;
    return route.fallback();
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');

  await page.locator('.conversation-list').getByRole('button', { name: 'manual · completed', exact: true }).click();
  await page.locator('.context-controls .document-disclosure > summary').click();
  await page.locator('.context-controls .file-disclosure > summary').click();
  await page.locator('.chat-surface .technical-disclosure > summary').click();
  await expect(page.getByLabel('Целевой документ', { exact: true })).toHaveValue('fixture-history');
  await expect(page.getByLabel('Каталог результатов', { exact: true })).toHaveValue(outputDirectory);
  await expect(page.locator('.document-disclosure > summary')).toContainText(documentName);
  await expect(page.getByRole('status')).toContainText('completed');
  await expect(page.getByTestId('save-destination')).toContainText('C:\\drawings\\history.cdr');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.getByRole('button', { name: 'Новая переписка', exact: true }).click();
  await expect(page.locator('.document-disclosure > summary')).toContainText('Документ не выбран');
  await expect(page.getByLabel('Целевой документ', { exact: true })).toHaveValue('');
  await expect(page.getByLabel('Каталог результатов', { exact: true })).toHaveValue(outputDirectory);
  expect(posts).toBe(0);
});

test('long mobile history stays bounded while navigation and the settings footer remain usable', async ({ page }) => {
  await mockApi(page, Array.from({ length: 100 }, (_, index) => ({
    id: `00000000-0000-0000-0000-${String(index + 10).padStart(12, '0')}`,
    conversation_id: `00000000-0000-0000-0000-${String(index + 1000).padStart(12, '0')}`,
    kind: 'manual', status: index === 99 ? 'stopped' : 'completed', last_seq: 1,
  })));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');

  const history = page.locator('.conversation-list');
  await expect(history.getByRole('button')).toHaveCount(100);
  const geometry = await history.evaluate(element => ({
    height: element.getBoundingClientRect().height,
    clientHeight: element.clientHeight,
    scrollHeight: element.scrollHeight,
    overflowY: getComputedStyle(element).overflowY,
  }));
  expect(geometry.height).toBeLessThan(844 / 4);
  expect(geometry.scrollHeight).toBeGreaterThan(geometry.clientHeight);
  expect(geometry.overflowY).toBe('auto');
  await expect(page.getByRole('button', { name: 'Новая переписка', exact: true })).toBeVisible();
  const settings = page.locator('.sidebar-footer').getByRole('button', { name: 'Настройки модели', exact: true });
  await expect(settings).toBeVisible();
  expect(await settings.evaluate(element => Math.abs(element.getBoundingClientRect().width - element.parentElement!.getBoundingClientRect().width))).toBeLessThan(1);
  await settings.click();
  await expect(page.getByRole('dialog')).toBeVisible();
  expect(await page.getByRole('dialog').evaluate(element => element.matches(':modal'))).toBe(true);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).not.toBeVisible();
  const oldest = history.getByRole('button', { name: 'manual · stopped', exact: true });
  await oldest.scrollIntoViewIfNeeded();
  expect(await history.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
  await oldest.click();
  await expect(page.getByRole('status')).toContainText('stopped');
});

for (const width of [390, 720]) {
  test(`long provider and destination strings wrap within a ${width}px shell without global clipping`, async ({ page }) => {
    const model = 'fixture-model-' + 'm'.repeat(400);
    const baseUrl = 'http://127.0.0.1:1234/v1/' + 'u'.repeat(1500);
    const path = 'C:\\drawings\\' + 'p'.repeat(1500) + '.cdr';
    await mockApi(page);
    await page.route('**/api/v1/profiles', route => route.fulfill({ json: { items: [{ id: '00000000-0000-0000-0000-000000000010', name: model, model, base_url: baseUrl, local: true }] } }));
    await page.route('**/api/v1/documents', route => route.fulfill({ json: { items: [{ document_id: 'fixture-long', name: 'Рисунок', path, active_page: 1 }], stale: false, at: null } }));
    await page.setViewportSize({ width, height: 844 });
    await page.goto('/');
    await page.locator('.context-controls .document-disclosure > summary').click();
    await page.locator('.chat-surface .technical-disclosure > summary').click();
    await page.getByLabel('Целевой документ', { exact: true }).selectOption('fixture-long');

    await expect(page.getByTestId('provider-scope')).toContainText(model);
    await expect(page.getByTestId('provider-scope')).toContainText(baseUrl);
    await expect(page.getByTestId('save-destination')).toContainText(path);
    for (const id of ['provider-scope', 'save-destination', 'job-stage']) {
      expect(await page.getByTestId(id).evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    expect(await page.evaluate(() => [document.documentElement, document.body].every(element => !['hidden', 'clip'].includes(getComputedStyle(element).overflowX)))).toBe(true);
  });
}
