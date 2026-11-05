import { expect, test } from '@playwright/test';

test('export uses selected operator root and structured output alias only', async ({ page }) => {
  let rootSaved = false; let exported = false;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B', path: 'C:\\drawings\\selected.cdr', active_page: 1 }], stale: false } });
    if (path.endsWith('/workspace')) { if (route.request().method() === 'PUT') { rootSaved = true; return route.fulfill({ json: route.request().postDataJSON() }); } return route.fulfill({ json: { output_roots: [] } }); }
    if (path.endsWith('/actions')) {
      const body = route.request().postDataJSON();
      expect(body.tool).toBe('corel_export'); expect(body.arguments.path).toBe('@output/result.svg');
      expect(body.arguments.format).toBe('svg'); expect(body.output_root_id).toBeTruthy(); expect(body.read_only).toBe(false);
      expect(body.arguments.document_id).toBeUndefined(); exported = true;
      return route.fulfill({ json: { id: '00000000-0000-0000-0000-000000000001', conversation_id: body.conversation_id, status: 'completed', last_seq: 0 } });
    }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [] } });
  });
  await page.goto('/');
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await page.getByLabel('Только чтение').uncheck();
  await page.getByLabel('Каталог результатов').fill('C:\\exports');
  await page.getByRole('button', { name: 'Разрешить каталог' }).click();
  await expect.poll(() => rootSaved).toBe(true);
  await page.getByLabel('Имя результата').fill('result.svg');
  await page.getByLabel('Формат экспорта').selectOption('svg');
  await page.getByRole('button', { name: 'Экспортировать' }).click();
  await expect.poll(() => exported).toBe(true);
  await page.goto('about:blank');
});

test('export choices never offer PDF selection or PNG SVG document', async ({ page }) => {
  await page.route('**/api/v1/**', route => route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43), items: [], stale: true } }));
  await page.goto('/');
  await page.getByLabel('Только чтение').uncheck();
  await expect(page.getByLabel('Область экспорта').locator('option[value="document"]')).toHaveCount(0);
  await page.getByLabel('Область экспорта').selectOption('selection');
  await page.getByLabel('Формат экспорта').selectOption('pdf');
  await expect(page.getByLabel('Область экспорта')).toHaveValue('page');
  await expect(page.getByLabel('Область экспорта').locator('option[value="selection"]')).toHaveCount(0);
  await page.getByLabel('Область экспорта').selectOption('document');
  await page.getByLabel('Формат экспорта').selectOption('svg');
  await expect(page.getByLabel('Область экспорта')).toHaveValue('page');
});
