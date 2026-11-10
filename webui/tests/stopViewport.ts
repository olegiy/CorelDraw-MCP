import { expect, type Page } from '@playwright/test';

export async function expectStatusAndStopInViewport(page: Page) {
  const context = page.locator('.context-bar');
  const status = context.getByRole('status');
  const stop = context.getByRole('button', { name: 'Стоп', exact: true });
  await expect(stop).toHaveCount(1);
  await expect(stop).toBeEnabled();
  const viewport = page.viewportSize()!;
  for (const control of [status, stop]) {
    const bounds = await control.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.y).toBeGreaterThanOrEqual(0);
    expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport.height);
    expect(bounds!.x).toBeGreaterThanOrEqual(0);
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width);
  }
  const stickyHeight = await context.evaluate(element => element.getBoundingClientRect().height);
  expect(stickyHeight).toBeLessThanOrEqual(96);
  for (const name of ['document-disclosure', 'file-disclosure']) {
    const disclosure = page.locator(`.context-controls .${name}`);
    expect(await disclosure.evaluate(element => element.getBoundingClientRect().bottom)).toBeLessThanOrEqual(stickyHeight);
  }
  expect(await stop.evaluate(element => {
    const bounds = element.getBoundingClientRect();
    return element.contains(document.elementFromPoint(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2));
  })).toBe(true);
  return (await stop.boundingBox())!;
}
