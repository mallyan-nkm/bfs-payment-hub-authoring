import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, startServer, STUDIO, type Server } from './server.js';

// Black box, in the browser: the content branch (R-09-19 to R-09-22, R-11-21, R-11-22). The studio
// shows the repository and branch its environment names; a draft is a commit on its change set's
// branch; a published change set is one commit on the content branch; pull-request mode opens a pull
// request and leaves the content branch alone.

const require = createRequire(import.meta.url);
let server: Server;
let browser: any;
let page: any;
const problems: string[] = [];
const commits = (branch = 'main') => server.fake.log(server.repo, branch).length;

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

const doc = (n: string) => ({
  kind: 'activity', name: `p.enrich.ui-git-note-${n}.v1`, release: '1.0.0', title: 'Note', type: 'enrich', reads: ['header'], writes: ['facts.note'],
  outcomes: [{ name: 'done', primary: true }], effect: 'pure', implementation: { binding: 'builtin', service: 'noop' },
  provenance: { origin: 'authored', status: 'provisional', clauses: ['UI git test'] },
});

describe('the content branch in the web app', () => {
  it('shows the repository and branch the environment names, and never the token', async () => {
    await page.goto(`${server.url}/#/export`);
    await page.waitForLoadState('networkidle');
    await expect.poll(() => page.getByTestId('git-remote').count(), { timeout: 20000 }).toBe(1);
    expect(await page.getByTestId('git-remote').textContent()).toBe(server.repo);
    expect(await page.getByTestId('git-branch').textContent()).toBe('main');
    expect(await page.content()).not.toContain('synthetic-test-token');
  }, 60000);

  it('a draft is a commit on its change set branch; approval is one commit on the content branch', async () => {
    const api = client(server.url);
    const before = commits();
    const d = await api.post('/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: doc('a') });
    expect(d.status, JSON.stringify(d.data)).toBe(200);
    const branch = server.fake.branches(server.repo).find((b) => b.startsWith('studio/'))!;
    expect(branch).toMatch(/^studio\/CS-\d{4}-\d\d-\d\d-\d{3}$/);
    expect(commits()).toBe(before);
    await page.reload();
    await page.waitForLoadState('networkidle');
    expect(await page.getByTestId('git-drafts').locator('tr', { hasText: branch }).count()).toBe(1);

    await api.post('/api/review', { tenant: 'owner', actor: 'shared.designer', key: d.data.draft.key });
    const pub = await api.post('/api/publish', { tenant: 'owner', key: d.data.draft.key, checker: 'shared.checker' });
    expect(pub.data.git?.status, JSON.stringify(pub.data.git)).toBe('committed');
    expect(commits()).toBe(before + 1);
    expect(server.fake.files(server.repo, 'main')[`content-configurations/change-sets/${pub.data.changeSet.id}.yaml`]).toBeDefined();

    await page.reload();
    await page.waitForLoadState('networkidle');
    expect(await page.getByTestId('git-change-sets').locator('tr', { hasText: pub.data.changeSet.id }).getByText('written').count()).toBe(1);
  }, 90000);

  it('in pull-request mode, approval opens a pull request and the content branch is not written', async () => {
    await page.getByLabel('Act as').selectOption('shared.checker');
    await page.getByLabel('Mode').selectOption('pull-request');
    await page.getByRole('button', { name: 'Save settings' }).click();
    await expect.poll(async () => (await page.locator('[role="status"]').first().textContent()) ?? '', { timeout: 20000 }).toMatch(/Saved: pull-request/);
    const api = client(server.url);
    const d = await api.post('/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: doc('b') });
    const before = commits();
    const pub = await api.post('/api/publish', { tenant: 'owner', key: d.data.draft.key, checker: 'shared.checker' });
    expect(pub.data.git, JSON.stringify(pub.data)).toMatchObject({ status: 'pull-request', branch: `studio/${pub.data.changeSet.id}`, pullRequest: { opened: true } });
    expect(commits()).toBe(before);
    expect(server.fake.repo(server.repo).pulls.at(-1)).toMatchObject({ head: `studio/${pub.data.changeSet.id}`, base: 'main', state: 'open' });
    expect(problems).toEqual([]);
  }, 90000);
});
