import { execSync } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { startServer, STUDIO, type Server } from './server.js';

// Black box: the studio's web app in a real browser (the pre-installed Chromium through Playwright).

const require = createRequire(import.meta.url);
const VIEWS: [string, string][] = [
  ['catalogue', 'Catalogue'], ['data', 'ISO data'], ['designer', 'Workflow designer'], ['editors', 'Artefact editors'], ['assistant', 'AI assistant'],
  ['tests', 'Test bench'], ['kit', 'Conformance kit'], ['tenant', 'Tenant'], ['export', 'Export'], ['governance', 'Governance'],
];

let server: Server;
let browser: any;
beforeAll(async () => {
  if (!fs.existsSync(path.join(STUDIO, 'web', 'dist', 'index.html'))) execSync('npm run web:build', { cwd: STUDIO, stdio: 'ignore' });
  server = await startServer();
  browser = await require('playwright').chromium.launch();
}, 180000);
afterAll(async () => {
  await browser?.close();
  server?.stop();
});

/** A page that records script errors and server errors as it goes. */
async function page(viewport = { width: 1280, height: 900 }) {
  const p = await browser.newPage({ viewport });
  const problems: string[] = [];
  p.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
  p.on('console', (m: any) => m.type() === 'error' && problems.push(`console: ${m.text()}`));
  p.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
  return { p, problems };
}

describe('the web app', () => {
  it.each(VIEWS)('opens the %s view with no script or server errors', async (view, heading) => {
    const { p, problems } = await page();
    await p.goto(`${server.url}/#/${view}`);
    await p.getByRole('heading', { name: heading, exact: true }).first().waitFor();
    await p.waitForLoadState('networkidle');
    expect(problems).toEqual([]);
    await p.close();
  });

  it.each(VIEWS)('fits the %s view on a phone, with no horizontal scroll', async (view, heading) => {
    const { p } = await page({ width: 390, height: 844 });
    await p.goto(`${server.url}/#/${view}`);
    await p.getByRole('heading', { name: heading, exact: true }).first().waitFor();
    await p.waitForLoadState('networkidle');
    const overflow = await p.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(1);
    await p.close();
  });

  it('searches the catalogue and opens an artefact', async () => {
    const { p, problems } = await page();
    await p.goto(`${server.url}/#/catalogue`);
    await p.getByRole('searchbox', { name: 'Search' }).fill('iban');
    await p.waitForTimeout(500);
    const hrefs: string[] = await p.$$eval('a[href^="#/artefact/"]', (els: any[]) => els.map((e) => e.getAttribute('href')));
    // The search matches names and the data paths an artefact reads, so not every hit names IBAN.
    expect(hrefs).toContain('#/artefact/uae.p.validate.iban.v1');
    await p.getByRole('searchbox', { name: 'Search' }).fill('');
    await p.waitForTimeout(500);
    expect(await p.locator('a[href^="#/artefact/"]').count()).toBeGreaterThan(hrefs.length);
    await p.click('a[href="#/artefact/uae.p.validate.iban.v1"]');
    await p.getByText('uae.p.validate.iban.v1').first().waitFor();
    expect(p.url()).toContain('#/artefact/uae.p.validate.iban.v1');
    expect(problems).toEqual([]);
    await p.close();
  });

  it('runs a test from the test bench and shows it passing', async () => {
    const { p, problems } = await page();
    await p.goto(`${server.url}/#/tests`);
    const row = p.locator('tr', { hasText: 'WPS-OUT-001' }).first();
    await row.getByRole('button', { name: 'Run' }).click();
    await expect.poll(async () => (await row.textContent()) ?? '', { timeout: 60000 }).toMatch(/pass/i);
    expect(problems).toEqual([]);
    await p.close();
  }, 90000);

  it('runs the whole conformance kit from the browser', async () => {
    const { p, problems } = await page();
    await p.goto(`${server.url}/#/kit`);
    await p.getByRole('button', { name: 'Run all 46' }).click();
    await expect.poll(async () => (await p.locator('body').textContent()) ?? '', { timeout: 120000 }).toMatch(/46 of 46/);
    expect(problems).toEqual([]);
    await p.close();
  }, 150000);

  it('offers prompt templates in the assistant, and holds the request until every blank is filled', async () => {
    const { p, problems } = await page();
    await p.goto(`${server.url}/#/assistant`);
    await p.getByLabel('Draft kind').selectOption('rule');
    const options = await p.getByLabel('Prompt template').locator('option').allTextContents();
    expect(options.length).toBeGreaterThanOrEqual(5);
    await p.getByLabel('Prompt template').selectOption('rule-limit');
    const text = await p.getByLabel('Instruction').inputValue();
    expect(text).toContain('<scope>.rule.<name>.v1');
    await expect.poll(async () => (await p.locator('[role="status"]').allTextContents()).join(' ')).toContain('Still to fill');
    expect(await p.getByRole('button', { name: 'Ask for a draft' }).isDisabled()).toBe(true);
    await p.getByLabel('Instruction').fill('Write a policy rule named b.rule.file-limit.v1 comparing each line and the file total with a parameter fileLimit, default 100000, which the bank sets. Above it, reject with AM02.');
    expect(await p.getByRole('button', { name: 'Ask for a draft' }).isDisabled()).toBe(false);
    expect(problems).toEqual([]);
    await p.close();
  });

  it('marks a user who may not author or approve', async () => {
    const { p } = await page();
    await p.goto(`${server.url}/#/catalogue`);
    await p.getByLabel('Tenant').selectOption('example-bank');
    await p.getByLabel('Acting as').selectOption('example.auditor');
    await p.getByText('read and review only').waitFor();
    await p.getByLabel('Acting as').selectOption('example.maker');
    await expect.poll(async () => p.getByText('read and review only').count()).toBe(0);
    await p.close();
  });
});
