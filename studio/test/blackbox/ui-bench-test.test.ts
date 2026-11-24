import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, signInPage, startServer, STUDIO, type Server } from './server.js';

// Black box, in the browser: a run on the ad hoc bench saved as a test (F-16, F-18). The saved test is a draft a checker can
// approve: it carries no field the test schema does not allow (a test has an id, not a release), so its change set has no error.

const require = createRequire(import.meta.url);
let server: Server;
let browser: any;
let page: any;

beforeAll(async () => {
  if (!fs.existsSync(path.join(STUDIO, 'web', 'dist', 'index.html'))) spawnSync('npm', ['run', 'web:build'], { cwd: STUDIO });
  server = await startServer();
  browser = await require('playwright').chromium.launch();
  page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.addInitScript(() => localStorage.setItem('th.dock', '0'));
  await signInPage(page, server.url, 'shared.designer');
}, 180000);
afterAll(async () => {
  await browser?.close();
  server?.stop();
});

describe('a bench run saved as a test', () => {
  it('saves a test draft with no error, which a checker approves', async () => {
    await page.goto(`${server.url}/#/tests`);
    await page.waitForLoadState('networkidle');
    await page.getByRole('tab', { name: 'Ad hoc bench' }).click();
    await page.getByLabel('Target', { exact: true }).selectOption('uae.wps.wf.salary-file-out.v1');
    for (const [port, sim] of [['uae.wps.port.wps.v1', 'uae.wps.sim.wps-outward.v1'], ['uae.p.port.calendar.v1', 'uae.p.sim.calendar.v1']]) {
      await page.getByRole('button', { name: 'Add a port' }).click();
      await page.getByLabel('Port', { exact: true }).last().selectOption(port);
      await page.getByLabel('Simulator', { exact: true }).last().selectOption(sim);
      await page.getByLabel('Scenario', { exact: true }).last().selectOption('accept');
    }
    await page.getByRole('tab', { name: 'Inbound sample' }).click();
    await page.getByLabel('Inbound sample').selectOption('samples/wps-sif-two-lines.sif');
    await page.getByRole('button', { name: 'Run the bench' }).click();
    await page.getByText('Save the run as a test (assertions)').waitFor();
    await page.getByLabel('Test id').fill('T-UI-BENCH-001');
    await page.getByLabel('Test title').fill('A salary file leg, saved from the bench');
    await page.getByRole('button', { name: /^Save as a test draft as / }).click();
    await page.getByText(/Saved the test draft/).waitFor();
    const api = client(server.url, { as: 'shared.designer' });
    const drafts = (await api.get('/api/drafts')).data;
    const draft = drafts.find((d: any) => d.name === 'T-UI-BENCH-001');
    expect(draft, JSON.stringify(drafts)).toBeDefined();
    expect(draft.issues).toBe(0);
    expect((await api.post('/api/request-release', { key: draft.key })).status).toBe(200);
    const published = await client(server.url, { as: 'shared.checker' }).post('/api/publish', { key: draft.key });
    expect(published.status, JSON.stringify(published.data)).toBe(200);
    const run = await api.post('/api/tests/T-UI-BENCH-001/run');
    expect(run.data.passed, JSON.stringify(run.data.checks?.filter((c: any) => !c.passed))).toBe(true);
  }, 120000);
});
