import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as net from 'node:net';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

// End to end (R-05-73 to R-05-75, Issues I-201 and I-202): the upload page and the audit page in a real browser, against the
// console's API on PostgreSQL in a container. The upload page reads what the console's API offers (with no distribution active
// here, nothing); the answers to an upload (started, refused) are the console's own answer shapes, played to the page, since the
// API's forwarding to the File API is tested in TransactHub.Operations.Tests (UploadAndAuditTests). The audit page shows the
// console's own chain, newest first, filtered, each row with its hash, and says that it verifies. Synthetic data only.

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const CONSOLE = path.resolve(here, '..', '..');
const RUNTIME = path.resolve(CONSOLE, '..');
const REPO = path.resolve(RUNTIME, '..');
const PASSWORD = 'Th-Test-Only-1234';
const DB = `th-ops-e2e-upl-pg-${process.pid}`;
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
      Store__Provider: 'PostgreSql', Store__Connection: `Host=127.0.0.1;Port=${port};Username=postgres;Password=${PASSWORD};Database=th_ops_e2e_upl`,
      Operations__Environment: 'test', Operations__FileApi__Url: 'http://127.0.0.1:9/', Operations__FileApi__Folder: 'out', Operations__FileApi__TokenRef: 'env:TH_E2E_UNUSED_TOKEN', Operations__ServiceConsole: 'http://127.0.0.1:8482',
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

const dev = (sub: string, roles: string[]) => ({ 'X-TH-Dev-Identity': JSON.stringify({ sub, name: sub, roles }) });

describe('the upload page (R-05-73, R-05-74)', () => {
  it("offers the runtime bank and the active distribution's messages, and shows a start with its payment and a refusal with its runbook", async () => {
    const page = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    const problems: string[] = [];
    page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
    await signIn(page, /Sign in as Example operator/, 'upload');
    await page.getByRole('heading', { name: 'Upload a file' }).waitFor();
    // No distribution is active in this store: the page says so, from the console's API.
    await page.getByText(/No distribution of example-bank is active in test/).waitFor();

    // The console's answers when one is active (the shapes of GET and POST /ops/api/upload).
    const sent: string[] = [];
    await page.route('**/ops/api/upload', async (route: any) => {
      const req = route.request();
      if (req.method() === 'GET') return route.fulfill({ json: { fileApi: true, environment: 'test', targets: [{ tenant: 'example-bank', environment: 'test', distribution: 'dist/example-bank@2026.11.23-1', messages: [{ id: 'uae.demo.ack', title: 'Acknowledgement', scheme: 'uae.demo', kind: 'deliver' }, { id: 'uae.demo.credit-file', title: 'Credit file from the corporate customer', scheme: 'uae.demo', kind: 'start' }] }] } });
      const form = req.postData() ?? '';
      sent.push(form);
      return form.includes('DEMO-DUP')
        ? route.fulfill({ status: 409, json: { status: 'refused', file: 'f-2', code: 'file.Duplicate', error: 'The same file was received before', outcome: 'refused', httpStatus: 409, runbook: 'http://127.0.0.1:8482/#/service/errors/file.Duplicate' } })
        : route.fulfill({ json: { status: 'accepted', file: 'f-1', payment: 'pay-demo-1', distribution: 'dist/example-bank@2026.11.23-1', outcome: 'started', httpStatus: 202 } });
    });
    await page.reload();
    await page.getByText('dist/example-bank@2026.11.23-1').waitFor();
    expect(await page.getByLabel('Tenant').inputValue()).toBe('example-bank');
    // The message that starts a payment is offered first.
    expect(await page.getByLabel('Message').inputValue()).toBe('uae.demo.credit-file');
    await page.getByLabel('File').setInputFiles({ name: 'DEMO-0001.csv', mimeType: 'text/csv', buffer: Buffer.from('H,DEMO-0001,AED,1,1.00\n') });
    await page.getByRole('button', { name: 'Upload' }).click();
    const answer = page.getByTestId('upload-answer');
    await answer.filter({ hasText: 'started' }).waitFor();
    expect(await answer.textContent()).toContain('Kept as f-1 and its payment started on dist/example-bank@2026.11.23-1.');
    expect(await page.getByTestId('upload-payment').getAttribute('href')).toBe('#/payments/pay-demo-1');
    expect(sent[0]).toContain('uae.demo.credit-file');
    expect(sent[0]).toContain('DEMO-0001.csv');

    await page.getByLabel('File').setInputFiles({ name: 'DEMO-DUP.csv', mimeType: 'text/csv', buffer: Buffer.from('H,DEMO-DUP\n') });
    await page.getByRole('button', { name: 'Upload' }).click();
    await answer.filter({ hasText: 'refused' }).waitFor();
    expect(await answer.textContent()).toContain('Refused (kept as f-2): The same file was received before');
    expect(await page.getByTestId('error-link').getAttribute('href')).toBe('http://127.0.0.1:8482/#/service/errors/file.Duplicate');
    expect(problems).toEqual([]);
    await page.close();
  });
});

describe('the audit page (R-05-75)', () => {
  it("shows the console's chain to a supervisor, newest first, filtered by person and action, each row with its hash, and that it verifies", async () => {
    // Facts of the console's own: two exports by an operator, a rebuild a supervisor asks for.
    for (const q of ['scheme=uae.wps', 'scheme=uae.wps&status=ACSC']) expect((await fetch(`${apiUrl}/ops/api/reports/export.csv?${q}`, { headers: dev('ops.operator', ['example-bank:Operator']) })).status).toBe(200);
    expect((await fetch(`${apiUrl}/ops/api/rebuild`, { method: 'POST', headers: { ...dev('ops.supervisor', ['example-bank:Supervisor']), 'content-type': 'application/json' }, body: JSON.stringify({ remarks: 'after a restore (synthetic)' }) })).status).toBe(202);

    const page = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    const problems: string[] = [];
    page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
    page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
    await signIn(page, /Sign in as Example supervisor/, 'audit');
    await page.getByRole('heading', { name: 'Operations audit' }).waitFor();
    await page.getByTestId('chain').filter({ hasText: 'The chain verifies (3 facts)' }).waitFor();
    const rows = page.getByTestId('audit-row');
    expect(await rows.count()).toBe(3);
    const facts = await rows.evaluateAll((rs: any[]) => rs.map((r) => r.children[4].textContent));
    expect(facts).toEqual(['#3', '#2', '#1']);
    expect(await rows.first().textContent()).toContain('rebuild-read-model');
    for (const h of await rows.evaluateAll((rs: any[]) => rs.map((r) => r.lastElementChild.getAttribute('title')))) expect(h).toMatch(/^[0-9a-f]{64}$/);

    await page.getByLabel('Person').fill('ops.operator');
    await page.getByRole('button', { name: 'Filter' }).click();
    await until('the person filter', async () => (await rows.count()) === 2, 30000);
    await page.getByLabel('Person').fill('');
    await page.getByLabel('Action').fill('rebuild-read-model');
    await page.getByRole('button', { name: 'Filter' }).click();
    await until('the action filter', async () => (await rows.count()) === 1, 30000);
    expect(problems).toEqual([]);

    // An operator is refused the trail.
    await signIn(page, /Sign in as Example operator/, 'audit');
    await page.getByRole('alert').filter({ hasText: 'supervisors and bank approvers' }).waitFor();
    await page.close();
  });
});
