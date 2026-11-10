import { expect, test } from '@playwright/test';

test('stage 4 file disclosure retains controlled paths format and range across keyboard close and reopen', async ({ page }) => {
  let mutations = 0;
  await page.route('**/api/v1/**', route => {
    if (['POST', 'PUT'].includes(route.request().method())) mutations++;
    return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43), items: [], stale: true } });
  });
  await page.goto('/');
  const files = page.locator('.context-controls .file-disclosure');
  const trigger = files.locator(':scope > summary');
  await expect(page.getByLabel('Открыть локальный CDR', { exact: true })).not.toBeVisible();
  await trigger.focus();
  await page.keyboard.press('Enter');
  await files.getByLabel('Открыть локальный CDR', { exact: true }).fill("C:\\Рисунки\\CDR с пробелом'.cdr");
  await files.getByLabel('Каталог результатов', { exact: true }).fill('C:\\exports');
  await files.getByRole('combobox', { name: 'Формат экспорта', exact: true }).selectOption('pdf');
  await files.getByLabel('Имя результата', { exact: true }).fill('C:\\exports\\custom.pdf');
  await files.getByRole('combobox', { name: 'Область экспорта', exact: true }).selectOption('document');
  await expect(files.getByText('Существующие файлы Save As и экспорта заменяются автоматически.', { exact: true })).toBeVisible();
  await trigger.focus();
  await page.keyboard.press('Space');
  await expect(files.getByLabel('Имя результата', { exact: true })).not.toBeVisible();
  await page.keyboard.press('Enter');
  await expect(files.getByLabel('Открыть локальный CDR', { exact: true })).toHaveValue("C:\\Рисунки\\CDR с пробелом'.cdr");
  await expect(files.getByLabel('Каталог результатов', { exact: true })).toHaveValue('C:\\exports');
  await expect(files.getByLabel('Имя результата', { exact: true })).toHaveValue('C:\\exports\\custom.pdf');
  await expect(files.getByRole('combobox', { name: 'Формат экспорта', exact: true })).toHaveValue('pdf');
  await expect(files.getByRole('combobox', { name: 'Область экспорта', exact: true })).toHaveValue('document');
  expect(mutations).toBe(0);
});

test('export uses a destination directory without root permission or overwrite', async ({ page }) => {
  let rootSaved = false; let exported = false;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B', path: 'C:\\drawings\\selected.cdr', active_page: 1 }], stale: false } });
    if (path.endsWith('/workspace')) { if (route.request().method() === 'PUT') { rootSaved = true; expect(route.request().postDataJSON()).toEqual({ output_directory: 'C:\\exports' }); return route.fulfill({ json: route.request().postDataJSON() }); } return route.fulfill({ json: { output_directory: null } }); }
    if (path.endsWith('/actions')) {
      const body = route.request().postDataJSON();
      expect(body.tool).toBe('corel_export'); expect(body.arguments.path).toBe('C:\\exports\\result.svg');
      expect(body.arguments.format).toBe('svg'); expect(body.output_directory).toBe('C:\\exports');
      expect(body.arguments.overwrite).toBeUndefined(); expect(body.output_root_id).toBeUndefined();
      expect(body.arguments.document_id).toBeUndefined(); exported = true;
      return route.fulfill({ json: { id: '00000000-0000-0000-0000-000000000001', conversation_id: body.conversation_id, status: 'completed', last_seq: 0 } });
    }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [] } });
  });
  await page.goto('/');
  await page.locator('.context-controls .document-disclosure > summary').click();
  await page.getByLabel('Целевой документ').selectOption('fixture-b');
  await page.locator('.context-controls .file-disclosure > summary').click();
  await page.getByLabel('Каталог результатов').fill('C:\\exports');
  await page.getByRole('button', { name: 'Выбрать каталог' }).click();
  await expect.poll(() => rootSaved).toBe(true);
  await page.getByLabel('Имя результата').fill('result.svg');
  await page.getByLabel('Формат экспорта').selectOption('svg');
  await page.getByRole('button', { name: 'Экспортировать' }).click();
  await expect.poll(() => exported).toBe(true);
  await page.goto('about:blank');
});

test('open sends an absolute CDR directly and never registers a file', async ({ page }) => {
  let opened = false;
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    expect(path).not.toContain('/files/register');
    if (path.endsWith('/actions')) {
      const body = route.request().postDataJSON();
      expect(body.tool).toBe('corel_open_document');
      expect(body.arguments.path).toBe("C:\\Рисунки\\CDR с пробелом'.cdr");
      opened = true;
      return route.fulfill({ json: { id: crypto.randomUUID(), status: 'completed', last_seq: 0 } });
    }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43), items: [], stale: true } });
  });
  await page.goto('/');
  await page.locator('.context-controls .file-disclosure > summary').click();
  await page.getByLabel('Открыть локальный CDR').fill("C:\\Рисунки\\CDR с пробелом'.cdr");
  await page.getByRole('button', { name: 'Открыть CDR', exact: true }).click();
  await expect.poll(() => opened).toBe(true);
});

test('file disclosure preserves Save As absolute destination without duplicate save or overwrite parameters', async ({ page }) => {
  const submitted: Record<string, unknown>[] = [];
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/access/session')) return route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43) } });
    if (path.endsWith('/documents')) return route.fulfill({ json: { items: [{ document_id: 'fixture-b', name: 'Рисунок B' }], stale: false } });
    if (path.endsWith('/actions')) { const body = route.request().postDataJSON(); submitted.push(body); return route.fulfill({ json: { id: crypto.randomUUID(), conversation_id: body.conversation_id, status: 'completed', last_seq: 0 } }); }
    if (path.endsWith('/events')) return route.fulfill({ contentType: 'text/event-stream', body: '' });
    return route.fulfill({ json: { items: [], output_directory: null } });
  });
  await page.goto('/');
  await page.locator('.context-controls .document-disclosure > summary').click();
  await page.getByLabel('Целевой документ', { exact: true }).selectOption('fixture-b');
  const files = page.locator('.context-controls .file-disclosure');
  await files.locator(':scope > summary').click();
  await files.getByLabel('Имя результата', { exact: true }).fill('C:\\exports\\copy.cdr');
  await files.locator(':scope > summary').click();
  await files.locator(':scope > summary').click();
  await files.getByRole('button', { name: 'Сохранить как CDR', exact: true }).click();
  await expect(page.locator('.context-bar').getByRole('status')).toContainText('completed');
  expect(submitted).toHaveLength(1);
  expect(submitted[0]).toMatchObject({ tool: 'corel_save_document', document_id: 'fixture-b', output_directory: null });
  expect(submitted[0].arguments).toEqual({ path: 'C:\\exports\\copy.cdr' });
});

test('export choices never offer PDF selection or PNG SVG document', async ({ page }) => {
  await page.route('**/api/v1/**', route => route.fulfill({ json: { authenticated: true, csrf_token: 'b'.repeat(43), items: [], stale: true } }));
  await page.goto('/');
  await page.locator('.context-controls .file-disclosure > summary').click();
  await expect(page.getByLabel('Область экспорта').locator('option[value="document"]')).toHaveCount(0);
  await page.getByLabel('Область экспорта').selectOption('selection');
  await page.getByLabel('Формат экспорта').selectOption('pdf');
  await expect(page.getByLabel('Область экспорта')).toHaveValue('page');
  await expect(page.getByLabel('Область экспорта').locator('option[value="selection"]')).toHaveCount(0);
  await page.getByLabel('Область экспорта').selectOption('document');
  await page.getByLabel('Формат экспорта').selectOption('svg');
  await expect(page.getByLabel('Область экспорта')).toHaveValue('page');
});
