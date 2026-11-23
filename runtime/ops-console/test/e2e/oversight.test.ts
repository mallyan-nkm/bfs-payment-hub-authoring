import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { createRequire } from 'node:module';
import * as net from 'node:net';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

// End to end (R-05-90 to R-05-92; owner's decisions of 23 November 2026, Issues I-184 and I-189): the fraud alert list, the bank's
// ledger accounts and the "rates stale" warning in a real browser, against the console's API on PostgreSQL in a container. The
// ledger page and the refusals are the API's own answers; an alert and stale rates are the API's answer shapes played to the page
// (the API's behaviour is tested in TransactHub.Operations.Tests: FraudAlertTests, LedgerAccountTests, FxRateStatusTests), and
// what the page sends back is read. Synthetic data only.

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const CONSOLE = path.resolve(here, '..', '..');
const RUNTIME = path.resolve(CONSOLE, '..');
const REPO = path.resolve(RUNTIME, '..');
const PASSWORD = 'Th-Test-Only-1234';
const DB = `th-ops-e2e-ovs-pg-${process.pid}`;
const procs: ChildProcess[] = [];
const logs: Record<string, string> = {};
let browser: any;
let apiUrl = '';

const docker = (...a: string[]) => spawnSync('docker', a, { encoding: 'utf8' });
const dotnetEnv = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };

function freePort(): Promise<number> {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.listen(0, '127.0.0.1', () => { const p = (s.address() as net.AddressInfo).port; s.close(() => resolve(p)); });
  });
}

async function until<T>(what: string, f: () => Promise<T | undefined | null | false>, ms = 120000): Promise<T> {
  const end = Date.now() + ms;
  for (;;) {
    try { const v = await f(); if (v) return v; } catch { /* not yet */ }
    if (Date.now() > end) throw new Error(`timed out waiting for ${what}\n${Object.entries(logs).map(([k, v]) => `--- ${k}\n${v.slice(-2000)}`).join('\n')}`);
    await new Promise((r) => setTimeout(r, 500));
  }
}

function run(cmd: string, args: string[], cwd: string, env: NodeJS.ProcessEnv = process.env): Promise<{ status: number | null; out: string }> {
  return new Promise((resolve) => {
    let out = '';
    const p = spawn(cmd, args, { cwd, env });
    p.stdout.on('data', (d) => (out += d));
    p.stderr.on('data', (d) => (out += d));
    p.on('close', (status) => resolve({ status, out }));
  });
}

beforeAll(async () => {
  const built = await run('npx', ['vite', 'build'], CONSOLE);
  if (built.status !== 0) throw new Error(built.out);
  const b = await run('dotnet', ['build', 'src/TransactHub.Operations.Api', '-c', 'Release', '--nologo', '-v', 'q'], RUNTIME, dotnetEnv);
  if (b.status !== 0) throw new Error(b.out);
  const created = docker('run', '-d', '--name', DB, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine');
  if (created.status !== 0) throw new Error(created.stderr);
  await until('PostgreSQL', async () => docker('exec', DB, 'pg_isready', '-U', 'postgres').status === 0 && docker('exec', DB, 'psql', '-U', 'postgres', '-c', 'select 1').status === 0);
  const port = docker('port', DB, '5432').stdout.split('\n')[0].split(':').pop()!.trim();
  apiUrl = `http://127.0.0.1:${await freePort()}`;
  logs.api = '';
  const p = spawn('dotnet', [path.join(RUNTIME, 'src/TransactHub.Operations.Api/bin/Release/net10.0/TransactHub.Operations.Api.dll')], {
    cwd: REPO,
    env: {
      ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: apiUrl, TH_AUTH_DEV: '1',
      Store__Provider: 'PostgreSql', Store__Connection: `Host=127.0.0.1;Port=${port};Username=postgres;Password=${PASSWORD};Database=th_ops_e2e_ovs`,
      Operations__Views: path.join(REPO, 'content-configurations', 'artefacts'), Operations__Console: path.join(CONSOLE, 'dist'),
    } as NodeJS.ProcessEnv,
  });
  p.stdout?.on('data', (d) => (logs.api += d));
  p.stderr?.on('data', (d) => (logs.api += d));
  procs.push(p);
  await until('the console API', async () => (await fetch(`${apiUrl}/ops/api/health`)).ok);
  browser = await require('playwright').chromium.launch();
}, 900000);

afterAll(async () => {
  await browser?.close();
  for (const p of procs) if (p.exitCode === null) p.kill('SIGKILL');
  docker('rm', '-f', '-v', DB);
});

async function signIn(page: any, who: RegExp, at: string) {
  await page.goto(apiUrl);
  await page.evaluate(() => sessionStorage.clear());
  await page.goto(apiUrl);
  await page.getByRole('button', { name: who }).click();
  await page.getByTestId('who').waitFor();
  await page.goto(`${apiUrl}/#/${at}`);
}

function watch(page: any): string[] {
  const problems: string[] = [];
  page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
  page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
  return problems;
}

const held = {
  item: 'B-SUSPECT-0001', itemKind: 'alert', tenant: 'example-bank', scheme: 'uae.wps', payment: 'B-SUSPECT-0001', link: '/payments/B-SUSPECT-0001',
  reason: 'FRAD', step: 'fraud', raisedAt: '2026-11-23T08:00:00Z', state: 'held', status: null, closed: false, amount: 1500, currency: 'AED',
  assignee: null, assigneeName: null, escalated: false, level: 0,
};

describe('the fraud alert list (R-05-90)', () => {
  it("lists a suspect payment with its reason and its link, and releases or refuses it through the payment's own action", async () => {
    const page = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    const problems = watch(page);
    await signIn(page, /Sign in as Example operator/, 'alerts');
    await page.getByRole('heading', { name: 'Fraud alerts' }).waitFor();
    // The API's own answer for this empty store.
    await page.getByTestId('alert-total').filter({ hasText: '0 alert(s), 0 open' }).waitFor();

    const sent: { url: string; body: any }[] = [];
    let state = 'held';
    await page.route('**/ops/api/alerts**', async (route: any) => {
      const req = route.request();
      if (req.method() === 'POST') {
        sent.push({ url: req.url(), body: JSON.parse(req.postData() ?? '{}') });
        state = sent.at(-1)!.body.action === 'release' ? 'released' : 'refused';
        return route.fulfill({ json: { action: 'a-1', status: 'done', alert: held.item, asked: sent.at(-1)!.body.action === 'release' ? 'approve' : 'decline' } });
      }
      return route.fulfill({ json: { total: 1, open: state === 'released' ? 0 : 1, rows: [{ ...held, state }] } });
    });
    await page.reload();
    const row = page.getByTestId('alert-row');
    await row.first().waitFor();
    expect(await page.getByTestId('alert-reason').textContent()).toBe('FRAD at fraud');
    expect(await page.getByTestId('alert-state').textContent()).toBe('Payment held');
    expect(await page.getByTestId('alert-link').getAttribute('href')).toBe('#/payments/B-SUSPECT-0001');
    // Refusing needs remarks; releasing asks the payment's own action, which the API answers with the view's rules.
    expect(await page.getByRole('button', { name: 'Refuse payment' }).isDisabled()).toBe(true);
    await page.getByRole('textbox', { name: 'Remarks' }).fill('The employer confirmed the file (synthetic)');
    await page.getByRole('button', { name: 'Release payment' }).click();
    await page.getByTestId('alert-note').filter({ hasText: 'Release of the payment asked' }).waitFor();
    expect(sent[0].url).toContain('/ops/api/alerts/B-SUSPECT-0001/payment-action');
    expect(sent[0].body).toEqual({ action: 'release', remarks: 'The employer confirmed the file (synthetic)' });
    await page.getByTestId('alert-state').filter({ hasText: 'Payment released' }).waitFor();
    expect(await page.getByRole('button', { name: 'Release payment' }).count()).toBe(0);
    // Taking the alert goes to the work queue's own change, which the API refuses here: no such alert in this store.
    await page.getByRole('button', { name: 'Take' }).click();
    await page.getByRole('alert').filter({ hasText: 'No alert B-SUSPECT-0001' }).waitFor();
    expect(problems.filter((p) => !p.includes('/ops/api/work'))).toEqual([]);
    await page.close();
  });
});

describe('the ledger accounts (R-05-91)', () => {
  it("shows a supervisor the bank's ledger accounts read-only, as its configuration names them, and refuses an operator", async () => {
    const page = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    const problems = watch(page);
    await signIn(page, /Sign in as Example supervisor/, 'ledger');
    await page.getByRole('heading', { name: 'Ledger accounts' }).waitFor();
    await page.getByRole('heading', { name: 'example-bank' }).waitFor();
    // This store's configuration sets no account: each of the accounting templates' four is shown as not set, never guessed.
    const rows = page.getByTestId('ledger-row');
    await until('the ledger rows', async () => (await rows.count()) === 8, 30000);
    expect(await page.getByTestId('ledger-account').allTextContents()).toEqual(Array(8).fill('not set'));
    expect(await page.getByRole('table', { name: 'Ledger accounts of p.post.accounting.v2' }).locator('tbody tr td:first-child').allTextContents())
      .toEqual(['Settlement (nostro)', 'Suspense', 'Charge income', 'VAT']);
    // Read-only: the page has no field or button to change an account.
    expect(await page.locator('section:has(h1:text("Ledger accounts"))').locator('input, button, select, textarea').count()).toBe(0);
    expect(problems).toEqual([]);
    await signIn(page, /Sign in as Example operator/, 'ledger');
    await page.getByRole('alert').filter({ hasText: 'for supervisors' }).waitFor();
    await page.close();
  });
});

describe('the rates warning (R-05-92)', () => {
  it('says nothing while the rates are within the limit, and "rates stale" with the pair once one is older', async () => {
    const page = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    const problems = watch(page);
    await signIn(page, /Sign in as Example operator/, 'payments');
    await page.getByTestId('lag').waitFor();
    expect(await page.getByTestId('rates-stale').count()).toBe(0);
    await page.route('**/ops/api/fx-rates', (route: any) => route.fulfill({
      json: { stale: true, banks: [{ tenant: 'example-bank', at: '2026-11-23T08:00:00Z', limit: 'PT24H', limitSource: 'default', stale: true, stalePairs: ['AED/EUR'], code: 'fx.RateStale',
        rates: [{ pair: 'AED/EUR', rate: 0.2511, asOf: '2026-11-21T09:00:00Z', age: 'P1DT23H', stale: true, standing: false }] }] },
    }));
    await page.reload();
    const badge = page.getByTestId('rates-stale');
    await badge.waitFor();
    expect(await badge.textContent()).toBe('Rates stale (example-bank: AED/EUR older than PT24H)');
    expect(problems).toEqual([]);
    await page.close();
  });
});
