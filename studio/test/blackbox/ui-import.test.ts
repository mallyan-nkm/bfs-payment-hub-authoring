import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, signInPage, startServer, STUDIO, type Server } from './server.js';

// Black box, in the browser: Import (I-33). The training bundle (WPS extracted as uae.wpsx) is
// previewed and imported as drafts; a broken bundle uploaded as a zip is refused with its errors.

const require = createRequire(import.meta.url);
let server: Server;
let browser: any;
let page: any;
const problems: string[] = [];

beforeAll(async () => {
  if (!fs.existsSync(path.join(STUDIO, 'web', 'dist', 'index.html'))) spawnSync('npm', ['run', 'web:build'], { cwd: STUDIO });
  server = await startServer();
  browser = await require('playwright').chromium.launch();
  page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.addInitScript(() => localStorage.setItem('th.dock', '0'));
  page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
  page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
  // A platform user imports the shared part; the bank part is left for the bank (R-11-30).
  await signInPage(page, server.url, 'shared.designer');
}, 180000);
afterAll(async () => {
  await browser?.close();
  server?.stop();
});

describe('import in the web app', () => {
  it('previews the training bundle: every source file accounted for, every test green', async () => {
    await page.goto(`${server.url}/#/import`);
    await page.waitForLoadState('networkidle');
    await page.getByRole('button', { name: 'Use the training bundle' }).click();
    await page.getByRole('button', { name: 'Preview' }).click();
    await page.getByTestId('import-summary').waitFor({ timeout: 60000 });
    expect(await page.getByText('may be imported').count()).toBe(1);
    const coverage = (await page.getByTestId('import-coverage').textContent()) ?? '';
    expect(coverage).toMatch(/Source files96/);
    expect(coverage).toMatch(/Neither0/);
    expect(await page.getByTestId('import-tests').locator('tbody tr').count()).toBe(18);
    expect(await page.getByTestId('import-tests').getByText('fail').count()).toBe(0);
    // The planned messages the extraction retired are listed, each with its reason.
    expect(await page.getByText(/Planned in the source, not built/).count()).toBe(3);
  }, 90000);

  it('imports as drafts, publishing nothing', async () => {
    await page.getByRole('button', { name: /^Import as drafts, as / }).click();
    await expect.poll(async () => (await page.locator('[role="status"]').first().textContent()) ?? '', { timeout: 60000 }).toMatch(/Imported \d+ as drafts.*Nothing is published: \d+ change sets? wait for an approver/);
    const drafts = await client(server.url).get('/api/drafts?tenant=owner');
    expect(drafts.data.some((d: any) => d.name === 'uae.wpsx.wf.salary-file-out.v1')).toBe(true);
    const cat = await client(server.url).get('/api/catalogue?q=uae.wpsx.wf.salary-file-out');
    expect(cat.data.items.filter((e: any) => e.status === 'published')).toEqual([]);
  }, 90000);

  it('refuses a broken bundle uploaded as a zip, and says why', async () => {
    // The training bundle, with one inventory item removed: a source file is no longer accounted for.
    const zip = await client(server.url).get('/api/import/sample');
    const { unzip, zip: makeZip } = await import('../../src/export/zip.js');
    const { parseYaml, toYaml } = await import('../../src/core/yaml.js');
    const entries = unzip(Buffer.from(zip.data)).map((e) => {
      if (e.name !== 'inventory.yaml') return e;
      const inv = parseYaml(e.content.toString('utf8'));
      inv.items = inv.items.slice(1);
      return { name: e.name, content: Buffer.from(toYaml(inv)) };
    });
    const file = path.join(fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'th-ui-import-')), 'broken.zip');
    fs.writeFileSync(file, makeZip(entries));
    await page.goto(`${server.url}/#/import`);
    await page.getByLabel('Extraction bundle').setInputFiles(file);
    await page.getByRole('button', { name: 'Preview' }).click();
    await page.getByTestId('import-summary').waitFor({ timeout: 60000 });
    expect((await page.getByTestId('import-issues').textContent()) ?? '').toMatch(/I64.*neither inventoried nor excluded/s);
    expect(await page.getByRole('button', { name: /^Import as drafts/ }).isDisabled()).toBe(true);
    expect(problems).toEqual([]);
  }, 90000);
});
