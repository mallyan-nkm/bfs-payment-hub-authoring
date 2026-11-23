import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
// Every released package is signed (R-09-23): a key of this run's own, from the studio's test support.
import '../../../../studio/test/support/signing-env';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

// End to end (R-05-50, R-06-24, R-05-56, R-05-57): a WPS salary file of two lines runs on the runtime (the runtime's build of
// the engine, DTFx by default, or the one TH_E2E_ENGINE names: dtfx, elsa, temporal or flowable; its claim check and its steps
// journalled to the store on PostgreSQL). The bank's internal limit holds it before release, and
// the hold is the runtime's (p.port.operations-queue.v1 given to the runtime): the native host opens it on the file's
// chain and answers held, and the engine awaits the operator's decision (R-05-56, Issue I-98). In a real browser an
// operator signs in, filters the list to held WPS files, opens the file, sees its declared fields, and approves the hold.
// The decision goes on the outbox; the engine's host reads it and delivers it to the waiting instance (R-05-57). The file
// then goes on: the WPS leg submits it, the acknowledgement comes back, and the console shows the leg with its CB response
// and, on the timeline, the instance's engine ids and the delivery. All values are synthetic.

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const CONSOLE = path.resolve(here, '..', '..');
const RUNTIME = path.resolve(CONSOLE, '..');
const REPO = path.resolve(RUNTIME, '..');
const STUDIO = path.join(REPO, 'studio');
const PASSWORD = 'Th-Test-Only-1234';
const DB = `th-ops-e2e-pg-${process.pid}`;
const ENGINE = (process.env.TH_E2E_ENGINE ?? 'dtfx') as 'dtfx' | 'elsa' | 'temporal' | 'flowable';
const FLOWABLE = `th-ops-e2e-flowable-${process.pid}`;
const FLOWABLE_USER = 'th-binding';
const FLOWABLE_PASSWORD = 'th-test-only';
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-ops-e2e-'));
const procs: ChildProcess[] = [];
const logs: Record<string, string> = {};
let browser: any;
let apiUrl = '';
let deployRun: Promise<string> | undefined;

const docker = (...a: string[]) => spawnSync('docker', a, { encoding: 'utf8' });
const dotnetEnv = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };

function freePort(): Promise<number> {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.listen(0, '127.0.0.1', () => { const p = (s.address() as net.AddressInfo).port; s.close(() => resolve(p)); });
  });
}

function start(name: string, cmd: string, args: string[], env: Record<string, string | undefined>, cwd = REPO) {
  logs[name] = '';
  const p = spawn(cmd, args, { cwd, env: { ...process.env, ...env } as NodeJS.ProcessEnv });
  p.stdout?.on('data', (d) => (logs[name] += d));
  p.stderr?.on('data', (d) => (logs[name] += d));
  procs.push(p);
  return p;
}

async function until<T>(what: string, f: () => Promise<T | undefined | null | false>, ms = 120000): Promise<T> {
  const end = Date.now() + ms;
  for (;;) {
    try { const v = await f(); if (v) return v; } catch { /* not yet */ }
    if (Date.now() > end) throw new Error(`timed out waiting for ${what}\n${Object.entries(logs).map(([k, v]) => `--- ${k}\n${v.slice(-2000)}`).join('\n')}`);
    await new Promise((r) => setTimeout(r, 500));
  }
}

/** A command run to its end without blocking the test worker (a blocked worker misses vitest's own heartbeats). */
function run(cmd: string, args: string[], cwd: string, env: NodeJS.ProcessEnv = process.env): Promise<{ status: number | null; out: string }> {
  return new Promise((resolve) => {
    let out = '';
    const p = spawn(cmd, args, { cwd, env });
    p.stdout.on('data', (d) => (out += d));
    p.stderr.on('data', (d) => (out += d));
    p.on('close', (status) => resolve({ status, out }));
  });
}

async function build(project: string) {
  const b = await run('dotnet', ['build', project, '-c', 'Release', '--nologo', '-v', 'q'], RUNTIME, dotnetEnv);
  if (b.status !== 0) throw new Error(`dotnet build ${project} failed:\n${b.out}`);
}

beforeAll(async () => {
  if (!fs.existsSync(path.join(CONSOLE, 'dist', 'index.html'))) await run('npx', ['vite', 'build'], CONSOLE);
  const harness = { dtfx: 'Dtfx', elsa: 'Elsa', temporal: 'Temporal', flowable: 'Flowable' }[ENGINE];
  for (const p of ['src/TransactHub.Operations.Api', 'src/TransactHub.Native', `tools/TransactHub.${harness}Harness`]) await build(p);
  // Flowable runs as its own server (plan D3): the Flowable REST server in a container of this run's own.
  let engineEnv: Record<string, string> = {};
  if (ENGINE === 'flowable') {
    const f = docker('run', '-d', '--name', FLOWABLE, '-e', 'FLOWABLE_ASYNC_EXECUTOR_ACTIVATE=false', '-e', 'FLOWABLE_ASYNC_HISTORY_EXECUTOR_ACTIVATE=false',
      '-e', 'FLOWABLE_REST_APP_CREATE_DEMO_DEFINITIONS=false', '-e', `FLOWABLE_REST_APP_ADMIN_USER_ID=${FLOWABLE_USER}`, '-e', `FLOWABLE_REST_APP_ADMIN_PASSWORD=${FLOWABLE_PASSWORD}`,
      '-p', '127.0.0.1::8080', process.env.TH_FLOWABLE_IMAGE ?? 'flowable/flowable-rest:7.1.0@sha256:b7ae287502cd5c243b577a0ca2894172e1c18cd75aea4fd67dc7d58f1b40db44');
    if (f.status !== 0) throw new Error(f.stderr);
    const fport = docker('port', FLOWABLE, '8080').stdout.split('\n')[0].split(':').pop()!.trim();
    const url = `http://127.0.0.1:${fport}/flowable-rest`;
    const auth = `Basic ${Buffer.from(`${FLOWABLE_USER}:${FLOWABLE_PASSWORD}`).toString('base64')}`;
    await until('the Flowable REST server', async () => (await fetch(`${url}/service/management/engine`, { headers: { authorization: auth } })).ok, 240000);
    engineEnv = { TH_FLOWABLE_URL: url, TH_FLOWABLE_USER: FLOWABLE_USER, TH_FLOWABLE_PASSWORD: FLOWABLE_PASSWORD };
  }
  // The store and the read model: one PostgreSQL, in a container of this run's own.
  const created = docker('run', '-d', '--name', DB, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine');
  if (created.status !== 0) throw new Error(created.stderr);
  await until('PostgreSQL', async () => docker('exec', DB, 'pg_isready', '-U', 'postgres').status === 0 && docker('exec', DB, 'psql', '-U', 'postgres', '-c', 'select 1').status === 0);
  const port = docker('port', DB, '5432').stdout.split('\n')[0].split(':').pop()!.trim();
  const connection = `Host=127.0.0.1;Port=${port};Username=postgres;Password=${PASSWORD};Database=th_ops_e2e`;
  const store = { TH_STORE_PROVIDER: 'PostgreSql', TH_STORE_CONNECTION: connection };

  // The console's API, with the dev sign-in (Development only), serving the built web app. It migrates the store.
  const apiPort = await freePort();
  apiUrl = `http://127.0.0.1:${apiPort}`;
  start('api', 'dotnet', [path.join(RUNTIME, 'src/TransactHub.Operations.Api/bin/Release/net10.0/TransactHub.Operations.Api.dll')], {
    ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: apiUrl, TH_AUTH_DEV: '1',
    Store__Provider: 'PostgreSql', Store__Connection: connection, Operations__PollMilliseconds: '200',
    Operations__Views: path.join(REPO, 'content-configurations', 'artefacts'), Operations__Console: path.join(CONSOLE, 'dist'),
  });
  await until('the console API', async () => (await fetch(`${apiUrl}/ops/api/health`)).ok);

  // The native host: it answers the operations-queue port's holds from the store.
  const nativePort = await freePort();
  start('native', 'dotnet', [path.join(RUNTIME, 'src/TransactHub.Native/bin/Release/net10.0/TransactHub.Native.dll')], {
    ...store, TH_NATIVE_URLS: `http://127.0.0.1:${nativePort}`,
  });
  await until('the native host', async () => (await fetch(`http://127.0.0.1:${nativePort}/health`)).ok);

  // The WPS file: the factory's corporate workflow exported for DTFx, the bank's internal limit lowered so a file of two lines is held.
  const tsx = path.join(STUDIO, 'node_modules', '.bin', 'tsx');
  const zip = path.join(work, `corporate-${ENGINE}.zip`);
  const ex = await run(tsx, ['src/cli.ts', 'export-zip', 'p.wf.corporate.v1', ENGINE, zip, '--with-simulators', '--tenant', 'example-bank'], STUDIO);
  if (ex.status !== 0) throw new Error(ex.out);
  const config = path.join(work, 'low-limit.yaml');
  fs.writeFileSync(config, fs.readFileSync(path.join(REPO, 'spec', 'examples', 'bank', 'configuration-set.yaml'), 'utf8').replace('value: 250000', 'value: 1000'));
  const bench = ['p.port.core-banking.v1=p.sim.core-banking.v1:accept', 'p.port.sanctions.v1=p.sim.sanctions.v1:accept', 'p.port.fraud.v1=p.sim.fraud.v1:accept',
    'uae.wps.port.wps.v1=uae.wps.sim.wps-outward.v1:accept', 'uae.p.port.calendar.v1=uae.p.sim.calendar.v1:accept', 'p.port.operations-queue.v1=runtime'];
  const child = start('deploy', tsx, ['src/cli.ts', 'deploy', zip, '--workflow', 'p.wf.corporate.v1', '--message', 'uae.wps.sif', '--inbound', path.join(STUDIO, 'fixtures', 'samples', 'wps-sif-two-lines.sif'),
    '--tenant', 'example-bank', '--config', config, ...bench.flatMap((b) => ['--bench', b])], { ...store, ...engineEnv, TH_NATIVE_HOST: `http://127.0.0.1:${nativePort}`, TH_OPERATOR_PATIENCE_SECONDS: '600' }, STUDIO);
  deployRun = new Promise((resolve) => child.on('close', () => resolve(logs.deploy)));
  browser = await require('playwright').chromium.launch();
}, 900000);

afterAll(async () => {
  await browser?.close();
  for (const p of procs) if (p.exitCode === null) p.kill('SIGKILL');
  docker('rm', '-f', '-v', DB);
  if (ENGINE === 'flowable') docker('rm', '-f', '-v', FLOWABLE);
  fs.rmSync(work, { recursive: true, force: true });
});

describe(`the operations console on a WPS file run through the runtime on ${ENGINE}`, () => {
  it('takes the held file in the work queue, which a supervisor sees live, and keeps a saved filter (R-05-71)', async () => {
    const op = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    const sup = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    const problems: string[] = [];
    for (const page of [op, sup]) {
      page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
      page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
    }
    await sup.goto(apiUrl);
    await sup.getByRole('button', { name: /Sign in as Example supervisor/ }).click();
    await op.goto(apiUrl);
    await op.getByRole('button', { name: /Sign in as Example operator/ }).click();
    await op.getByRole('heading', { name: 'Payments' }).waitFor();
    // The held file is a work item once its hold is in the read model.
    await until('the held file in the work queue', async () => {
      await op.goto(`${apiUrl}/#/work`);
      await op.reload();
      await op.getByRole('heading', { name: 'Work queue' }).waitFor();
      await op.getByTestId('work-total').filter({ hasText: 'item' }).waitFor();
      return (await op.getByTestId('work-row').count()) === 1;
    }, 300000);
    await sup.goto(`${apiUrl}/#/work`);
    await sup.getByTestId('work-row').first().waitFor();
    expect(await sup.getByTestId('work-holder').first().textContent()).toBe('Unassigned');

    // The operator takes it; the supervisor's queue follows without a reload.
    await op.getByTestId('work-row').first().getByRole('checkbox').check();
    await op.getByRole('button', { name: /^Take \(1\)$/ }).click();
    await op.getByTestId('work-note').filter({ hasText: '1 done, 0 refused' }).waitFor();
    await until('the supervisor\'s live queue', async () => (await sup.getByTestId('work-holder').first().textContent()) === 'Example operator', 60000);
    // Another taking it in bulk is refused for that item; the supervisor releases it.
    await sup.getByTestId('work-row').first().getByRole('checkbox').check();
    await sup.locator('input[name=work-remarks]').fill('back to the pool for the approval (synthetic)');
    await sup.getByRole('button', { name: 'Release' }).click();
    await sup.getByTestId('work-note').filter({ hasText: '1 done' }).waitFor();
    await until('the operator\'s live queue', async () => (await op.getByTestId('work-holder').first().textContent()) === 'Unassigned', 60000);

    // A saved filter: the operator's own.
    op.on('dialog', (d: any) => d.accept('Held WPS files'));
    await op.goto(`${apiUrl}/#/payments?scheme=uae.wps&hold=open`);
    await op.reload();
    await op.getByRole('heading', { name: 'Payments' }).waitFor();
    await op.getByRole('button', { name: 'Save this filter' }).click();
    await op.getByRole('button', { name: 'Remove saved filter Held WPS files' }).waitFor();
    await op.goto(`${apiUrl}/#/payments`);
    await op.reload();
    await op.getByRole('heading', { name: 'Payments' }).waitFor();
    await until('the saved filter offered', async () => (await op.locator('select[name=saved-filter] option').count()) === 2, 20000);
    await op.locator('select[name=saved-filter]').selectOption({ label: 'Held WPS files' });
    await until('the saved filter applied', async () => (await op.locator('select[name=hold]').inputValue()) === 'open' && (await op.getByTestId('payment-row').count()) === 1, 20000);
    expect(problems).toEqual([]);
    await op.close();
    await sup.close();
  });

  it('filters to held WPS files, opens one with its declared fields, approves the hold, and shows the leg it then runs', async () => {
    const page = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    const problems: string[] = [];
    page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
    page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
    await page.goto(apiUrl);
    await page.getByRole('button', { name: /Sign in as Example operator/ }).click();
    await page.getByRole('heading', { name: 'Payments' }).waitFor();

    // Filter: the WPS scheme, held, outward. The held file appears once the hold is in the read model.
    await until('the held file in the list', async () => {
      await page.goto(`${apiUrl}/#/payments?scheme=uae.wps&hold=open&direction=outward`);
      await page.reload();
      await page.getByRole('heading', { name: 'Payments' }).waitFor();
      return (await page.getByTestId('payment-row').count()) === 1;
    }, 240000);
    // The scheme's option comes with the list of schemes, a moment after the rows.
    await until('the scheme filter', async () => (await page.locator('select[name=scheme]').inputValue()) === 'uae.wps', 20000);
    expect(await page.locator('select[name=hold]').inputValue()).toBe('open');
    const row = page.getByTestId('payment-row').first();
    expect(await row.textContent()).toContain('1,500.00 AED');
    // A declared column of the scheme's view: the MOL establishment id, masked.
    expect(await page.getByRole('columnheader', { name: 'MOL establishment id' }).count()).toBe(1);
    expect(await row.textContent()).toContain('0000*****4567');
    await row.click();

    // The detail: the hold and why, the platform's and the scheme's declared fields, masked where the old screens masked.
    await page.getByTestId('hold-badge').waitFor();
    expect(await page.getByTestId('hold-badge').textContent()).toContain('AM02');
    const wps = page.getByTestId('fields-uae.wps.view.operations.v1');
    expect(await wps.locator('[data-field=employerId] dd').textContent()).toBe('0000*****4567');
    expect(await wps.locator('[data-field=salaryMonth] dd').textContent()).toBe('102026');
    expect(await wps.locator('[data-field=totalRecords] dd').textContent()).toBe('2');
    expect(await page.getByTestId('fields-p.view.operations.v1').locator('[data-field=sanctions] dd').textContent()).toBe('clear');
    await page.getByRole('tab', { name: /Lines/ }).click();
    expect(await page.getByRole('table', { name: 'Lines' }).textContent()).toContain('AE07***************3456');
    expect(await page.content()).not.toContain('AE070331234567890123456');
    await page.getByRole('tab', { name: /Timeline/ }).click();
    expect(await page.getByRole('list', { name: 'Timeline' }).textContent()).toContain('internal-limit');

    // Approve the hold: the operator's decision reaches the instance that awaits it, through the engine's host.
    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    await page.locator('textarea[name=remarks]').fill('Within the employer agreement (synthetic)');
    await page.getByRole('button', { name: 'Confirm approve' }).click();
    await page.getByRole('status').filter({ hasText: 'Approve: done' }).waitFor();

    // The file goes on: the deployment ends settled, through the hold approved and the WPS leg.
    const out = await Promise.race([deployRun!, new Promise<string>((_, rej) => setTimeout(() => rej(new Error('deploy did not finish')), 600000))]);
    expect(out, out.slice(-3000)).toMatch(/^outcome settled/m);
    expect(/^trace  (.*)$/m.exec(out)?.[1]).toContain('hook:hold | hold:approved | leg:settled');

    // The console shows the leg with its declared fields, and who approved.
    await until('the WPS leg in the console', async () => {
      await page.reload();
      await page.getByRole('tab', { name: /Legs \(1\)/ }).click();
      return (await page.getByTestId('leg').count()) === 1;
    }, 60000);
    const leg = page.getByTestId('leg').first();
    expect(await leg.getAttribute('data-leg')).toBe('uae.wps.wf.salary-file-out.v1');
    expect(await leg.locator('[data-field=cbResponse] dd').textContent()).toBe('ack');
    expect(await page.getByTestId('fields-p.view.operations.v1').locator('[data-field=approvedBy] dd').textContent()).toBe('operator:ops.operator');
    expect(await page.getByTestId('hold-badge').count()).toBe(0);
    expect(await page.getByTestId('lag').textContent()).toContain('up to date');
    // The timeline: the instance's engine ids when it started, and the decision delivered to it by the engine's host.
    await page.getByRole('tab', { name: /Timeline/ }).click();
    const timeline = await page.getByRole('list', { name: 'Timeline' }).textContent();
    expect(timeline).toContain('engine');
    expect(timeline).toContain('delivered');

    // The list filtered by the leg and its CB response, a declared filter, finds it.
    await page.goto(`${apiUrl}/#/payments?leg=uae.wps.wf.salary-file-out.v1&f:uae.wps.view.operations.v1:uae.wps.wf.salary-file-out.v1:cbResponse=ack`);
    await page.reload();
    await until('the leg filter', async () => (await page.getByTestId('payment-row').count()) === 1, 20000);
    expect(problems).toEqual([]);
    await page.close();
  });

  it('reports the file from the views and the read model, and exports it as CSV, masked (R-05-59)', async () => {
    const page = await browser.newPage({ viewport: { width: 1360, height: 900 }, acceptDownloads: true });
    const problems: string[] = [];
    page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
    page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
    await page.goto(apiUrl);
    await page.getByRole('button', { name: /Sign in as Example operator/ }).click();
    await page.goto(`${apiUrl}/#/reports`);
    await page.getByRole('heading', { name: 'Reports' }).waitFor();
    await until('the totals', async () => (await page.getByTestId('report-payments').textContent()) === '1', 20000);
    // The WPS view's declared measures, no WPS screen: its list columns of counts and amounts.
    const wps = page.getByRole('table', { name: 'Measures of uae.wps' });
    expect(await wps.textContent()).toContain('Total records');
    expect(await page.getByRole('table', { name: 'By scheme' }).textContent()).toContain('uae.wps');
    // A summary by a view's field.
    await page.getByLabel('Group by').selectOption('field:uae.wps.view.operations.v1:subject:salaryMonth');
    await until('the summary by salary month', async () => ((await page.getByRole('table', { name: /By field:/ }).textContent()) ?? '').includes('102026'), 20000);
    // The export: the list as CSV, with the declared columns, masked.
    const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Export CSV' }).click()]);
    const csv = fs.readFileSync(await download.path(), 'utf8');
    expect(csv.split('\r\n')[0]).toContain('Id,Kind,Tenant,Scheme');
    expect(csv).toContain('0000*****4567');
    expect(csv).not.toContain('AE070331234567890123456');
    // The WPS fixed reports the view declares (R-05-67): the SIF detail, one row per line, masked; the download an audit fact.
    await page.getByRole('table', { name: 'Fixed reports' }).waitFor();
    const [detail] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download SIF detail' }).click()]);
    const sif = fs.readFileSync(await detail.path(), 'utf8').split('\r\n').filter(Boolean);
    expect(sif[0]).toContain('Employee person id');
    expect(sif).toHaveLength(3);
    expect(sif.join('\n')).not.toContain('AE070331234567890123456');
    await page.getByTestId('fixed-report-note').filter({ hasText: 'SIF detail: 2 row(s), audit fact' }).waitFor();
    expect(problems).toEqual([]);
    await page.close();
  });
});
