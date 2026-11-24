import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, signInPage, startServer, STUDIO, type Server } from './server.js';

// Black box, in the browser: a draft run (R-09-230, Issue I-341). A designer runs an activity they have not published on the
// ad hoc bench with their drafts, sees the run marked as a draft run, saves it as a test draft in the activity's change set,
// and runs that test from the list, again as a draft run.

const require = createRequire(import.meta.url);
let server: Server;
let browser: any;
let page: any;

const ACT = {
  kind: 'activity', name: 'p.validate.ui-draft-period.v1', release: '1.0.0', title: 'The period of a file is in order', type: 'validate',
  reads: ['header'], writes: ['facts.period'], outcomes: [{ name: 'pass', primary: true }, { name: 'reject' }],
  effect: 'pure', implementation: { binding: 'script', language: 'typescript', script: 'scripts/p.validate.ui-draft-period.v1.ts' },
};
const SRC = "export async function run({ ctx }) {\n  const ok = /^\\d{4}-\\d{2}$/.test(String(ctx.header.period ?? ''));\n  ctx.facts = { ...(ctx.facts ?? {}), period: { ok } };\n  return ok ? 'pass' : { outcome: 'reject', reasonCode: 'DT01' };\n}\n";

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

describe('a draft run on the bench', () => {
  it('runs a draft activity with the designer\'s drafts, marked as a draft run, and its saved test too', async () => {
    const api = client(server.url, { as: 'shared.designer' });
    const saved = await api.post('/api/drafts', { doc: ACT });
    expect(saved.status, JSON.stringify(saved.data)).toBe(200);
    expect((await api.post('/api/scripts', { activity: ACT.name, source: SRC })).status).toBe(200);

    await page.goto(`${server.url}/#/tests`);
    await page.waitForLoadState('networkidle');
    await page.getByRole('tab', { name: 'Ad hoc bench' }).click();
    await page.getByRole('checkbox', { name: /Run with my drafts/ }).check();
    await page.locator(`select[aria-label="Target"] option[value="${ACT.name}"]`).waitFor({ state: 'attached' });
    await page.getByLabel('Target', { exact: true }).selectOption(ACT.name);
    await page.getByLabel('Context JSON').fill(JSON.stringify({ header: { paymentId: 'P-1', period: '2026-11' } }));
    await page.getByRole('button', { name: 'Run the bench' }).click();
    const note = page.locator('.draft-run');
    await note.waitFor();
    expect(await note.innerText()).toMatch(/Draft run\.[\s\S]*not published[\s\S]*p\.validate\.ui-draft-period\.v1@1\.0\.0/);

    // Saved as a test draft: it joins the activity's change set, is listed as a draft and runs as a draft run.
    await page.getByText('Save the run as a test (assertions)').waitFor();
    await page.getByLabel('Test id').fill('T-UI-DRAFT-001');
    await page.getByLabel('Test title').fill('A period in order, from a draft run');
    await page.getByRole('button', { name: /^Save as a test draft as / }).click();
    await page.getByText(/Saved the test draft/).waitFor();
    const drafts = (await api.get('/api/drafts')).data;
    const test = drafts.find((d: any) => d.name === 'T-UI-DRAFT-001');
    expect(test?.changeSet, JSON.stringify(drafts)).toBe(saved.data.draft.changeSet);

    await page.reload();
    await page.waitForLoadState('networkidle');
    await page.getByRole('tab', { name: 'Tests' }).click();
    const row = page.getByRole('row', { name: /T-UI-DRAFT-001/ });
    await row.getByText('draft', { exact: true }).waitFor();
    await row.getByRole('button', { name: 'Run', exact: true }).click();
    await page.locator('.draft-run').waitFor();
    const run = await api.post('/api/tests/T-UI-DRAFT-001/run');
    expect(run.data.passed, JSON.stringify(run.data.checks?.filter((c: any) => !c.passed))).toBe(true);
    expect(run.data.draftRun.drafts).toEqual(expect.arrayContaining([`${ACT.name}@1.0.0`, 'T-UI-DRAFT-001']));
  }, 180000);
});
