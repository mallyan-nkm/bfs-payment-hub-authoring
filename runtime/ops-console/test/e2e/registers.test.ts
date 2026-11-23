import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as net from 'node:net';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

// End to end (R-05-62 to R-05-64): the registers area in a real browser, against the console's API on PostgreSQL in a container.
// A register maker proposes a corporate; it is not in force and the maker may not consent to it; a different person with the bank
// approver's role consents, and the corporate is in force, its personal fields masked, its history showing both versions after a
// second change. Synthetic corporates only.

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const CONSOLE = path.resolve(here, '..', '..');
const RUNTIME = path.resolve(CONSOLE, '..');
const REPO = path.resolve(RUNTIME, '..');
const PASSWORD = 'Th-Test-Only-1234';
const DB = `th-ops-e2e-reg-pg-${process.pid}`;
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
      Store__Provider: 'PostgreSql', Store__Connection: `Host=127.0.0.1;Port=${port};Username=postgres;Password=${PASSWORD};Database=th_ops_e2e_reg`,
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

async function signIn(page: any, who: RegExp) {
  await page.goto(apiUrl);
  await page.evaluate(() => sessionStorage.clear());
  await page.goto(apiUrl);
  await page.getByRole('button', { name: who }).click();
  await page.goto(`${apiUrl}/#/registers/corporate`);
}

describe('the registers area: a maker proposes, a different bank approver consents (R-05-62 to R-05-64)', () => {
  it('keeps a proposed corporate out of force until a bank approver other than its maker consents, and shows every version masked', async () => {
    const page = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    const problems: string[] = [];
    page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
    page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));

    // The maker proposes a new corporate.
    await signIn(page, /Sign in as Example register maker/);
    await page.getByRole('heading', { name: 'Corporate registration' }).waitFor();
    await page.getByRole('button', { name: 'New corporate registration' }).click();
    const form = page.getByRole('form', { name: 'Propose a change' });
    await form.locator('input[name=corporateId]').fill('C-0001');
    await form.locator('input[name=name]').fill('Example Trading LLC');
    await form.locator('input[name=cif]').fill('CIF000111');
    await form.locator('input[name=establishmentId]').fill('0000001234567');
    await form.locator('input[name=accounts]').fill('AE070331234567890123456');
    await form.locator('input[name=contactPerson]').fill('Example Person');
    await form.locator('input[name=active]').check();
    await form.locator('input[name=wpsRegistrationFormHeld]').check();
    await form.getByRole('button', { name: 'Propose the change' }).click();
    await page.getByRole('status').filter({ hasText: 'Proposed' }).waitFor();
    expect(await page.getByTestId('record-row').count()).toBe(0);
    // The maker sees the change waiting, and may not consent to it.
    await page.goto(`${apiUrl}/#/registers/changes`);
    await page.getByTestId('change').first().waitFor();
    expect(await page.getByTestId('not-yours').count()).toBe(1);
    expect(await page.getByTestId('change').first().textContent()).toContain('0000*****4567');
    expect(await page.content()).not.toContain('Example Person');

    // A bank approver consents.
    await signIn(page, /Sign in as Example bank approver/);
    await page.goto(`${apiUrl}/#/registers/changes`);
    await page.getByTestId('change').first().waitFor();
    await page.getByRole('button', { name: 'Consent' }).click();
    await page.getByRole('status').filter({ hasText: 'Consented to' }).waitFor();

    // In force: the list and the record, masked as the operations views mask.
    await page.goto(`${apiUrl}/#/registers/corporate`);
    await until('the corporate in force', async () => { await page.reload(); return (await page.getByTestId('record-row').count()) === 1; }, 20000);
    await page.getByTestId('record-row').first().click();
    const record = page.getByTestId('record');
    await record.waitFor();
    expect(await record.locator('[data-field=cif] dd').textContent()).toBe('CIF0****0111');
    expect(await record.locator('[data-field=establishmentId] dd').textContent()).toBe('0000*****4567');
    expect(await record.locator('[data-field=name] dd').textContent()).toBe('Example Trading LLC');
    expect(await page.content()).not.toContain('AE070331234567890123456');

    // The maker changes the branch: a second version, waiting; the first stays in force.
    await signIn(page, /Sign in as Example register maker/);
    await page.goto(`${apiUrl}/#/registers/corporate/C-0001`);
    await page.getByRole('button', { name: 'Change', exact: true }).click();
    await page.getByRole('form', { name: 'Propose a change' }).locator('input[name=branch]').fill('002');
    await page.getByRole('button', { name: 'Propose the change' }).click();
    await page.getByTestId('waiting').waitFor();
    const states = await page.getByTestId('version').evaluateAll((rows: any[]) => rows.map((r) => r.getAttribute('data-state')));
    expect(states).toEqual(['pending', 'approved']);
    expect(await page.getByTestId('record').locator('[data-field=branch] dd').textContent()).toBe('—');
    expect(problems).toEqual([]);
    await page.close();
  });
});
