import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { initBareRepo } from '../../src/export/git.js';
import { client, startServer, STUDIO, type Server } from './server.js';

// Black box, in the browser: connect the studio to a branch (here a local bare repository), publish a
// change set, see it written, and export it again by hand, which changes nothing (R-09-19 to R-09-21).

const require = createRequire(import.meta.url);
let server: Server;
let browser: any;
let page: any;
const problems: string[] = [];
const remote = initBareRepo(fs.mkdtempSync(path.join(os.tmpdir(), 'th-ui-git-')));
const commits = () => spawnSync('git', ['--git-dir', remote, 'rev-list', '--count', 'main'], { encoding: 'utf8' }).stdout.trim();

beforeAll(async () => {
  if (!fs.existsSync(path.join(STUDIO, 'web', 'dist', 'index.html'))) spawnSync('npm', ['run', 'web:build'], { cwd: STUDIO });
  server = await startServer();
  browser = await require('playwright').chromium.launch();
  page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.addInitScript(() => localStorage.setItem('th.dock', '0'));
  page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
  page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
}, 180000);
afterAll(async () => {
  await browser?.close();
  server?.stop();
});

describe('export to a Git branch in the web app', () => {
  it('connects as a shared checker', async () => {
    await page.goto(`${server.url}/#/export`);
    await page.waitForLoadState('networkidle');
    await page.getByLabel('Act as').selectOption('shared.checker');
    await page.getByLabel('Repository').fill(remote);
    await page.getByLabel('Branch').fill('main');
    await page.getByRole('button', { name: 'Connect' }).click();
    await expect.poll(() => page.getByTestId('git-remote').count(), { timeout: 20000 }).toBe(1);
    expect(await page.getByTestId('git-remote').textContent()).toBe(remote);
  }, 60000);

  it('a published change set lands on the branch, and exporting it again changes nothing', async () => {
    const api = client(server.url);
    const doc = {
      kind: 'activity', name: 'p.enrich.ui-git-note.v1', release: '1.0.0', title: 'Note', type: 'enrich', reads: ['header'], writes: ['facts.note'],
      outcomes: [{ name: 'done', primary: true }], effect: 'pure', implementation: { binding: 'builtin', service: 'noop' },
      provenance: { origin: 'authored', status: 'provisional', clauses: ['UI git test'] },
    };
    const d = await api.post('/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc });
    await api.post('/api/review', { tenant: 'owner', actor: 'shared.designer', key: d.data.draft.key });
    const pub = await api.post('/api/publish', { tenant: 'owner', key: d.data.draft.key, checker: 'shared.checker' });
    expect(pub.data.git?.status, JSON.stringify(pub.data.git)).toBe('committed');
    expect(commits()).toBe('2');

    await page.reload();
    await page.waitForLoadState('networkidle');
    const row = page.getByTestId('git-change-sets').locator('tr', { hasText: pub.data.changeSet.id });
    expect(await row.getByText('written').count()).toBe(1);
    await page.getByLabel('Act as').selectOption('shared.checker');
    await row.getByRole('button', { name: 'Export' }).click();
    await expect.poll(async () => (await page.locator('[role="status"]').first().textContent()) ?? '', { timeout: 20000 }).toMatch(/already on the branch/);
    expect(commits()).toBe('2');
    expect(problems).toEqual([]);
  }, 90000);
});
