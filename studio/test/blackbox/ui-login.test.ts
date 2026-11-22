import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { startServer, STUDIO, type Server } from './server.js';

// Black box, in the browser: sign-in, sign-out, and the view each role lands in (R-10-08, R-11-32).
// The studio runs with the built-in development provider (TH_AUTH_DEV=1), whose sign-in page lists
// the studio's users.

const require = createRequire(import.meta.url);
let server: Server;
let browser: any;
beforeAll(async () => {
  if (!fs.existsSync(path.join(STUDIO, 'web', 'dist', 'index.html'))) spawnSync('npm', ['run', 'web:build'], { cwd: STUDIO });
  server = await startServer();
  browser = await require('playwright').chromium.launch();
}, 180000);
afterAll(async () => {
  await browser?.close();
  server?.stop();
});

async function fresh() {
  const p = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const problems: string[] = [];
  p.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
  p.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
  return { p, problems };
}

/** Sign in from the studio's own page, as a person does. */
async function signInFromStudio(p: any, user: string) {
  await p.goto(`${server.url}/`);
  await p.getByRole('link', { name: 'Sign in' }).click();
  await p.getByRole('heading', { name: 'Development sign-in' }).waitFor();
  await p.getByRole('button', { name: user, exact: true }).click();
  await p.getByLabel('Signed in as').waitFor();
}

const nav = async (p: any) => (await p.locator('nav.sidebar a.nav > span:first-child').allTextContents()) as string[];

describe('sign-in in the web app', () => {
  it('shows only the sign-in page before sign-in, and nothing of the studio', async () => {
    const { p, problems } = await fresh();
    await p.goto(`${server.url}/#/catalogue`);
    await p.getByRole('link', { name: 'Sign in' }).waitFor();
    expect(await p.locator('nav.sidebar').count()).toBe(0);
    expect(await p.getByText('built-in development sign-in').count()).toBe(1);
    expect(problems.filter((x) => !/401/.test(x))).toEqual([]);
    await p.close();
  });

  it.each([
    ['example.maker', 'Bank workspace', '#/bank', ['Bank workspace', 'Workflow designer', 'Catalogue', 'Test bench', 'Tenant'], ['Conformance kit']],
    ['example.checker', 'Governance', '#/governance', ['Bank workspace', 'Governance'], ['Workflow designer', 'Artefact editors', 'Import']],
    ['workflow.designer', 'Workflow designer', '#/designer', ['Workflow designer', 'Catalogue', 'Test bench'], ['Bank workspace', 'Tenant']],
    ['workflow.developer', 'Artefact editors', '#/editors', ['Artefact editors', 'Catalogue', 'Test bench'], ['Bank workspace', 'Workflow designer', 'Tenant']],
    ['shared.checker', 'Governance', '#/governance', ['Governance', 'Conformance kit'], ['Workflow designer', 'Artefact editors', 'Bank workspace', 'Import']],
    ['users.admin', 'Governance', '#/governance', ['Governance'], ['Test bench', 'Workflow designer', 'Export']],
    ['shared.auditor', 'Catalogue', '#/catalogue', ['Catalogue'], ['Workflow designer', 'Import', 'Export']],
  ])('signs %s in and lands on %s, with only its surfaces', async (user, heading, hash, shows, hides) => {
    const { p, problems } = await fresh();
    await signInFromStudio(p, user);
    await p.getByRole('heading', { name: heading, exact: true }).first().waitFor();
    expect(p.url()).toContain(hash);
    expect(await p.getByLabel('Signed in as').textContent()).toContain(user);
    const items = await nav(p);
    for (const s of shows) expect(items).toContain(s);
    for (const h of hides) expect(items).not.toContain(h);
    expect(await p.getByLabel('Acting as').count()).toBe(0);
    await p.waitForLoadState('networkidle');
    expect(problems).toEqual([]);
    await p.close();
  });

  it('shows a bank its workspace: hooks, rules, dials, simulators, bindings and the test bench by workflow', async () => {
    const { p, problems } = await fresh();
    await signInFromStudio(p, 'example.maker');
    for (const s of ['Hooks', 'Rules and rule values', 'Simulators and scenarios', 'Dials', 'Hook bindings', 'Test bench by workflow']) await p.getByRole('heading', { name: s }).first().waitFor();
    await p.locator('a[href="#/tests?bench=p.wf.corporate.v1"]').first().waitFor();
    expect(await p.getByText('b.hook.pre-release-approval.v1').count()).toBeGreaterThan(0);
    // A surface that is not the role's says so; the server would refuse the change anyway.
    await p.goto(`${server.url}/#/kit`);
    await p.getByRole('heading', { name: 'Not for your role' }).waitFor();
    expect(problems).toEqual([]);
    await p.close();
  });

  it('signs out: the session ends and the API answers 401', async () => {
    const { p } = await fresh();
    await signInFromStudio(p, 'workflow.designer');
    await p.getByRole('button', { name: 'Sign out' }).click();
    await p.getByRole('link', { name: 'Sign in' }).waitFor();
    const status = await p.evaluate(async () => (await fetch('/api/catalogue')).status);
    expect(status).toBe(401);
    await p.close();
  });

  it('tells an identity that is no user of the studio so, and shows nothing else', async () => {
    const { p } = await fresh();
    await p.goto(`${server.url}/auth/login`);
    // The development provider takes any identity; this one is in no entry of governance/users.yaml.
    await p.evaluate(() => {
      const f = document.querySelector('form')!;
      const b = document.createElement('button');
      b.name = 'user';
      b.value = 'nobody.known';
      b.textContent = 'nobody.known';
      f.appendChild(b);
    });
    await p.getByRole('button', { name: 'nobody.known' }).click();
    await p.getByText('who is not a user of the studio').waitFor();
    expect(await p.locator('nav.sidebar').count()).toBe(0);
    await p.close();
  });
});
