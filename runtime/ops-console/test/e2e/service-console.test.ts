import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as http from 'node:http';
import { createRequire } from 'node:module';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
// Every released package is signed (R-09-23): a key of this run's own, from the studio's test support.
import '../../../../studio/test/support/signing-env';
import { unzip } from '../../../../studio/src/export/zip';
import { signFiles } from '../../../../studio/src/export/signing';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

// End to end (R-06-25, R-09-39, R-09-40, R-05-52, R-09-43): the service console's API host with the store on PostgreSQL in
// a container of this run's own, and GitHub as a fake of the four Git Data API endpoints the package source reads, holding
// the studio's corporate workflow exported for Temporal and signed with this run's key, and a version whose tag names
// another release. In a real browser a service engineer signs in, checks health, installs the signed version, sees the
// other refused with its error code, opens the runbook from the error, and confirms an action (re-verify the package).

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const CONSOLE = path.resolve(here, '..', '..');
const RUNTIME = path.resolve(CONSOLE, '..');
const REPO = path.resolve(RUNTIME, '..');
const STUDIO = path.join(REPO, 'studio');
const PASSWORD = 'Th-Test-Only-1234';
const DB = `th-svc-e2e-pg-${process.pid}`;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-svc-e2e-'));
const procs: ChildProcess[] = [];
const logs: Record<string, string> = {};
let browser: any;
let apiUrl = '';
let gh: http.Server | undefined;

const docker = (...a: string[]) => spawnSync('docker', a, { encoding: 'utf8' });
const dotnetEnv = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };

function freePort(): Promise<number> {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.listen(0, '127.0.0.1', () => { const p = (s.address() as net.AddressInfo).port; s.close(() => resolve(p)); });
  });
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

async function until<T>(what: string, f: () => Promise<T | undefined | null | false>, ms = 120000): Promise<T> {
  const end = Date.now() + ms;
  for (;;) {
    try { const v = await f(); if (v) return v; } catch { /* not yet */ }
    if (Date.now() > end) throw new Error(`timed out waiting for ${what}\n${Object.entries(logs).map(([k, v]) => `--- ${k}\n${v.slice(-3000)}`).join('\n')}`);
    await new Promise((r) => setTimeout(r, 500));
  }
}

/** GitHub's Git Data API for one repository, bank/content, as the package source reads it. */
function fakeGitHub(published: { tag: string; folder: string; files: Record<string, Buffer> }[]) {
  const sha = (b: Buffer | string) => createHash('sha1').update(b).digest('hex');
  const commits = new Map<string, Record<string, Buffer>>();
  const tags = new Map<string, { commit: string; message: string }>();
  const refs = new Map<string, string>();
  const blobs = new Map<string, Buffer>();
  for (const p of published) {
    const all: Record<string, Buffer> = {};
    for (const [k, v] of Object.entries(p.files)) { all[`${p.folder}/${k}`] = v; blobs.set(sha(v), v); }
    const commit = sha(Object.keys(all).sort().join('|') + p.tag);
    commits.set(commit, all);
    const t = sha(`tag ${p.tag}`);
    tags.set(t, { commit, message: `Release ${p.tag}` });
    refs.set(p.tag, t);
  }
  return http.createServer((req, res) => {
    const u = new URL(req.url ?? '/', 'http://x');
    const rest = decodeURIComponent(u.pathname.replace(/^\/repos\/bank\/content\//, ''));
    let body: unknown;
    const m = /^git\/matching-refs\/tags\/(release|dist|config)$/.exec(rest);
    if (m) body = [...refs].filter(([k]) => k.startsWith(m[1])).sort().map(([k, v]) => ({ ref: `refs/tags/${k}`, object: { sha: v, type: 'tag' } }));
    else if (rest.startsWith('git/tags/') && tags.has(rest.slice(9))) { const t = tags.get(rest.slice(9))!; body = { message: t.message, object: { sha: t.commit, type: 'commit' } }; }
    else if (rest.startsWith('git/trees/') && commits.has(rest.slice(10))) body = { truncated: false, tree: Object.entries(commits.get(rest.slice(10))!).map(([p, v]) => ({ path: p, type: 'blob', sha: sha(v) })) };
    else if (rest.startsWith('git/blobs/') && blobs.has(rest.slice(10))) body = { encoding: 'base64', content: blobs.get(rest.slice(10))!.toString('base64') };
    res.writeHead(body === undefined ? 404 : 200, { 'content-type': 'application/json' });
    res.end(JSON.stringify(body ?? { message: 'Not Found' }));
  });
}

/** A small distribution as the studio builds it: its files, a manifest with the bill of materials and the parameter contract, signed. */
function distribution(id: string, bank = 'example-bank'): Record<string, Buffer> {
  const files: Record<string, string> = { 'binding.yaml': 'kind: engine-binding\ntarget: temporal\n', 'workflows/p.wf.corporate.v1@1.0.0.yaml': 'kind: workflow\nname: p.wf.corporate.v1\nrelease: 1.0.0\n' };
  const sha = (t: string) => `sha256:${createHash('sha256').update(t).digest('hex')}`;
  const billOfMaterials = { packs: [{ scope: 'p', release: '1.1.0' }], artefacts: Object.entries(files).map(([p, t]) => ({ name: p.split('/').pop()!.split('@')[0], kind: p.startsWith('binding') ? 'engine-binding' : 'workflow', release: '1.0.0', path: p, sha256: sha(t) })).sort((a, b) => a.path.localeCompare(b.path)) };
  const parameterContract = { format: 'th-parameter-contract/1', parameters: [] };
  files['manifest.yaml'] = JSON.stringify({ kind: 'distribution', id, bank, engine: 'temporal', schemes: ['uae.wps'], longestWait: 'P30D', billOfMaterials, parameterContract });
  const signed = signFiles(files, { kind: 'distribution', releaseId: `dist/${bank}@${id}`, workflow: '', release: id, target: 'temporal', tenant: bank, changeSet: 'CS-SYNTHETIC-1', extra: { billOfMaterials, parameterContract } });
  return Object.fromEntries(Object.entries(signed).map(([k, v]) => [k, Buffer.from(v)]));
}

beforeAll(async () => {
  if (!fs.existsSync(path.join(CONSOLE, 'dist', 'index.html'))) await run('npx', ['vite', 'build'], CONSOLE);
  const b = await run('dotnet', ['build', 'src/TransactHub.Service.Api', '-c', 'Release', '--nologo', '-v', 'q'], RUNTIME, dotnetEnv);
  if (b.status !== 0) throw new Error(`dotnet build failed:\n${b.out}`);

  // The studio's corporate workflow for Temporal, signed with this run's key: the version GitHub offers.
  const tsx = path.join(STUDIO, 'node_modules', '.bin', 'tsx');
  const zipFile = path.join(work, 'corporate-temporal.zip');
  const ex = await run(tsx, ['src/cli.ts', 'export-zip', 'p.wf.corporate.v1', 'temporal', zipFile], STUDIO);
  if (ex.status !== 0) throw new Error(ex.out);
  const files = Object.fromEntries(unzip(fs.readFileSync(zipFile)).map((e) => [e.name, e.content]));
  const release = JSON.parse(files['release.json'].toString()).releaseId as string; // p.wf.corporate.v1/temporal@1.0.0
  const r = release.split('@')[1];
  const ghPort = await freePort();
  gh = fakeGitHub([
    { tag: `release/p.wf.corporate.v1/${r}/temporal`, folder: `content-configurations/releases/p/p.wf.corporate.v1/${r}/temporal`, files },
    // A bank's distribution (R-09-48, R-09-49), signed with this run's key: offered, verified, installed (Issue I-102 done).
    { tag: 'dist/example-bank/2026.11.22-1', folder: 'content-configurations/releases/distributions/example-bank/2026.11.22-1', files: distribution('2026.11.22-1') },
    // Another bank's distribution, signed by the same key: listed, never installable in this bank's runtime (R-09-76).
    { tag: 'dist/second-bank/2026.11.22-1', folder: 'content-configurations/releases/distributions/second-bank/2026.11.22-1', files: distribution('2026.11.22-1', 'second-bank') },
    // A tag that names another release than the package in its folder: refused.
    { tag: 'release/p.wf.corporate.v1/9.9.9/temporal', folder: 'content-configurations/releases/p/p.wf.corporate.v1/9.9.9/temporal', files },
  ]);
  await new Promise<void>((resolve) => gh!.listen(ghPort, '127.0.0.1', resolve));

  const created = docker('run', '-d', '--name', DB, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine');
  if (created.status !== 0) throw new Error(created.stderr);
  await until('PostgreSQL', async () => docker('exec', DB, 'pg_isready', '-U', 'postgres').status === 0 && docker('exec', DB, 'psql', '-U', 'postgres', '-c', 'select 1').status === 0);
  const port = docker('port', DB, '5432').stdout.split('\n')[0].split(':').pop()!.trim();
  const connection = `Host=127.0.0.1;Port=${port};Username=postgres;Password=${PASSWORD};Database=th_svc_e2e`;

  const apiPort = await freePort();
  apiUrl = `http://127.0.0.1:${apiPort}`;
  logs.api = '';
  const p = spawn('dotnet', [path.join(RUNTIME, 'src/TransactHub.Service.Api/bin/Release/net10.0/TransactHub.Service.Api.dll')], {
    cwd: REPO,
    env: {
      ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: apiUrl, TH_AUTH_DEV: '1', TH_BANK: 'example-bank',
      Store__Provider: 'PostgreSql', Store__Connection: connection, Service__Project: 'true', Service__Environment: 'production',
      PackageSource__GitHub__Repository: 'bank/content', PackageSource__GitHub__Api: `http://127.0.0.1:${ghPort}/`,
      Service__Errors: path.join(REPO, 'content-configurations', 'artefacts'), Service__Docs: path.join(REPO, 'docs', 'north-star'),
      Service__Console: path.join(CONSOLE, 'dist'), Service__Dashboards__Payments: 'http://127.0.0.1:3000/d/payments',
    } as NodeJS.ProcessEnv,
  });
  p.stdout?.on('data', (d) => (logs.api += d));
  p.stderr?.on('data', (d) => (logs.api += d));
  procs.push(p);
  await until('the service console API', async () => (await fetch(`${apiUrl}/service/api/auth/config`)).ok);
  browser = await require('playwright').chromium.launch();
}, 900000);

afterAll(async () => {
  await browser?.close();
  for (const p of procs) if (p.exitCode === null) p.kill('SIGKILL');
  gh?.close();
  docker('rm', '-f', '-v', DB);
  fs.rmSync(work, { recursive: true, force: true });
});

describe('the service console', () => {
  it('signs in a service engineer, shows health, installs a signed version from GitHub once a bank approver consents, links a refusal to its runbook, confirms an action, and has a pause of intake and a worker\'s drain consented to', async () => {
    const page = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    const problems: string[] = [];
    page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
    page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
    // The bank approver, another person, consents to every change of the system the engineer asks for (R-09-77).
    const approver = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    approver.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
    await approver.goto(apiUrl);
    await approver.getByRole('button', { name: /Sign in as Example bank approver/ }).click();
    await approver.getByTestId('who').filter({ hasText: 'Example bank approver' }).waitFor();
    const consent = async (action: string) => {
      await approver.goto(`${apiUrl}/#/service/actions`);
      await approver.reload();
      const row = approver.getByTestId('pending-row').filter({ hasText: action }).first();
      await row.waitFor();
      await row.getByRole('button', { name: 'Approve' }).click();
      await approver.locator('textarea[name=reason]').fill('Agreed with the operations manager (synthetic)');
      await approver.getByRole('button', { name: 'Confirm approval' }).click();
      const r = approver.getByTestId('result').filter({ hasText: action });
      await r.waitFor();
      expect(await r.locator('.badge').textContent()).toBe('done');
    };
    await page.goto(apiUrl);
    await page.getByRole('button', { name: /Sign in as Example service engineer/ }).click();
    await page.getByTestId('who').filter({ hasText: 'Example service engineer' }).waitFor();

    // Health: the store, the read model, the package source and the trusted keys answer.
    await page.goto(`${apiUrl}/#/service/health`);
    await page.getByRole('heading', { name: 'Health' }).waitFor();
    for (const name of ['store', 'read-model', 'package-source', 'trusted-signers']) {
      const row = page.locator(`[data-testid=health-row][data-name="${name}"]`);
      await row.waitFor();
      expect(await row.locator('.badge').first().textContent()).toBe('ok');
    }

    // Content: the signed version is offered, verified and installed, with a reason.
    await page.goto(`${apiUrl}/#/service/content`);
    const good = page.locator('[data-testid=version-row][data-tag="release/p.wf.corporate.v1/1.0.0/temporal"]');
    await good.waitFor();
    expect(await good.locator('.badge').textContent()).toBe('offered');
    await good.getByRole('button', { name: 'Install' }).click();
    await page.locator('textarea[name=reason]').fill('CR-0001: first release of the corporate workflow (synthetic)');
    await page.getByRole('button', { name: 'Confirm install 1.0.0' }).click();
    // Verified now; stored once a bank approver consents.
    await page.getByTestId('result').filter({ hasText: 'install' }).filter({ hasText: 'waits for the consent of a bank approver' }).waitFor();
    expect(await page.locator('[data-testid=release-row][data-release="p.wf.corporate.v1/temporal@1.0.0"]').count()).toBe(0);
    await consent('install');
    await page.goto(`${apiUrl}/#/service/content`);
    await page.reload();
    const installed = page.locator('[data-testid=release-row][data-release="p.wf.corporate.v1/temporal@1.0.0"]');
    await installed.waitFor();
    expect(await installed.locator('.badge').textContent()).toBe('installed');

    // The other version is refused, and its error links to its runbook.
    const bad = page.locator('[data-testid=version-row][data-tag="release/p.wf.corporate.v1/9.9.9/temporal"]');
    await bad.getByRole('button', { name: 'Install' }).click();
    await page.locator('textarea[name=reason]').fill('Try the other tag (synthetic)');
    await page.getByRole('button', { name: 'Confirm install 9.9.9' }).click();
    const alert = page.getByRole('alert');
    await alert.waitFor();
    expect(await alert.textContent()).toContain('names p.wf.corporate.v1/temporal@9.9.9, but its package is p.wf.corporate.v1/temporal@1.0.0');
    await alert.getByTestId('error-link').click();
    await page.getByTestId('runbook').waitFor();
    expect(await page.getByRole('heading', { level: 1 }).textContent()).toContain('signing.WrongRelease');
    expect(await page.getByRole('heading', { name: 'Checks' }).count()).toBe(1);
    expect(await page.getByTestId('runbook').textContent()).toContain('Escalation');

    // The refusal is in the event log, as a fact with who and why, linked to the same runbook.
    await page.goto(`${apiUrl}/#/service/logs?code=signing.WrongRelease`);
    await page.reload();
    await until('the refusal in the log', async () => (await page.getByTestId('log-row').count()) > 0, 20000);
    expect(await page.getByTestId('log-row').first().getByTestId('error-link').textContent()).toBe('signing.WrongRelease');

    // A bank's distribution: offered with its bank, installed through the verification of its kind, shown with its open payments.
    await page.goto(`${apiUrl}/#/service/content`);
    const dist = page.locator('[data-testid=version-row][data-tag="dist/example-bank/2026.11.22-1"]');
    await dist.waitFor();
    expect(await dist.textContent()).toContain('example-bank');
    await dist.getByRole('button', { name: 'Difference' }).click();
    await page.getByTestId('diff').filter({ hasText: 'dist/example-bank@2026.11.22-1 against nothing active' }).waitFor();
    await dist.getByRole('button', { name: 'Install' }).click();
    await page.locator('textarea[name=reason]').fill('CR-0002: the first distribution of the example bank (synthetic)');
    await page.getByRole('button', { name: 'Confirm install 2026.11.22-1' }).click();
    await page.getByTestId('result').filter({ hasText: 'waits for the consent of a bank approver' }).waitFor();
    await consent('install');
    await page.goto(`${apiUrl}/#/service/content`);
    await page.reload();
    // The other bank's distribution is listed and never installable here.
    const other = page.locator('[data-testid=version-row][data-tag="dist/second-bank/2026.11.22-1"]');
    await other.waitFor();
    expect(await other.textContent()).toContain('this runtime serves example-bank only');
    expect(await other.getByRole('button', { name: 'Install' }).count()).toBe(0);
    const distRow = page.locator('[data-testid=release-row][data-release="dist/example-bank@2026.11.22-1"]');
    await distRow.waitFor();
    expect(await distRow.locator('.badge').textContent()).toBe('installed');
    expect(await distRow.textContent()).toContain('distribution');
    expect(await distRow.locator('.num').textContent()).toBe('0');

    // An action, confirmed by a person with a reason: re-verify the installed package against the keys trusted now.
    await page.goto(`${apiUrl}/#/service/content`);
    await installed.waitFor();
    await installed.getByRole('button', { name: 'Re-verify' }).click();
    await page.locator('textarea[name=reason]').fill('Weekly check of installed packages (synthetic)');
    await page.getByRole('button', { name: 'Confirm re-verify 1.0.0' }).click();
    const done = page.getByTestId('result').filter({ hasText: 'reverify-package' });
    await done.waitFor();
    expect(await done.locator('.badge').textContent()).toBe('done');

    // Every troubleshooting action is built (Issue I-101): none is refused for want of a path.
    await page.goto(`${apiUrl}/#/service/actions`);
    await page.getByRole('heading', { name: 'Troubleshooting actions' }).waitFor();
    const catalogue = page.getByRole('table', { name: 'Catalogue' });
    await catalogue.locator('tr', { hasText: 'restart-worker' }).waitFor();
    expect(await catalogue.textContent()).not.toContain('missing');
    for (const a of ['retry-attempt', 'redrive-workflow', 'reset-activity', 'resend-file', 'expire-hold', 'reconcile-posting', 'pause-intake', 'drain-worker'])
      expect(await catalogue.locator('tr', { hasText: a }).locator('.badge').textContent()).toBe('ok');

    // Intake of a scheme paused and a worker drained (R-09-62, R-09-63): the engineer asks, a bank approver consents, each reaches its target.
    const calls: string[] = [];
    const worker = http.createServer((req, res) => { calls.push(`${req.method} ${req.url}`); res.setHeader('content-type', 'application/json'); res.end(JSON.stringify({ draining: true, inFlight: 0 })); });
    await new Promise<void>((r) => worker.listen(0, '127.0.0.1', () => r()));
    const workerUrl = `http://127.0.0.1:${(worker.address() as net.AddressInfo).port}`;
    const ask = async (action: string, title: string, target: string, parameters?: any) => {
      await page.goto(`${apiUrl}/#/service/actions`);
      await page.reload();
      await page.locator('select[name=action]').selectOption(action);
      await page.locator('input[name=target]').fill(target);
      if (parameters) await page.locator('input[name=parameters]').fill(JSON.stringify(parameters));
      await page.getByRole('button', { name: 'Ask for it' }).click();
      await page.locator('textarea[name=reason]').fill(`${title}: the scheme's window is closed (synthetic)`);
      await page.getByRole('button', { name: `Confirm ${title.toLowerCase()}` }).click();
      await page.getByTestId('result').filter({ hasText: action }).filter({ hasText: 'waits for the consent of a bank approver' }).waitFor();
    };
    await ask('pause-intake', 'Pause intake for a scheme', 'uae.wps', { tenant: 'example-bank' });
    await ask('drain-worker', 'Drain a worker', workerUrl);
    for (const action of ['pause-intake', 'drain-worker']) {
      await approver.goto(`${apiUrl}/#/service/actions`);
      await approver.reload();
      const row = approver.getByTestId('pending-row').filter({ hasText: action });
      await row.waitFor();
      await row.getByRole('button', { name: 'Approve' }).click();
      await approver.locator('textarea[name=reason]').fill('Agreed with the operations manager (synthetic)');
      await approver.getByRole('button', { name: 'Confirm approval' }).click();
      const r = approver.getByTestId('result').filter({ hasText: action });
      await r.waitFor();
      expect(await r.locator('.badge').textContent()).toBe('done');
    }
    expect(calls).toEqual(['POST /drain']);
    await approver.close();
    worker.close();

    const audit = await (await fetch(`${apiUrl}/service/api/audit`, { headers: { 'X-TH-Dev-Identity': JSON.stringify({ sub: 'svc.engineer', name: 'Example service engineer', roles: ['*:ServiceEngineer'] }) } })).json();
    const codes = audit.map((f: any) => f.code);
    expect(codes).toEqual(expect.arrayContaining(['install', 'action.requested', 'action.done']));
    // Installed on the bank approver's consent; asked for by the engineer.
    expect(audit.filter((f: any) => f.code === 'install').every((f: any) => f.actor === 'service:bank.approver' && f.data.why)).toBe(true);
    expect(audit.filter((f: any) => f.code === 'action.requested' && f.data.name === 'install').every((f: any) => f.actor === 'service:svc.engineer')).toBe(true);
    expect(audit.filter((f: any) => f.code === 'action.approved').every((f: any) => f.actor === 'service:bank.approver' && f.data.requestedBy === 'service:svc.engineer')).toBe(true);
    expect(problems).toEqual([]);
    await page.close();
  });
});
