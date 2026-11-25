import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { toYaml } from '../../src/core/yaml.js';
import { accept, runPinned, type RuntimeClient } from '../../src/deploy/front-door.js';
import { client, startServer, STUDIO, type Server } from './server.js';
import { runtimeApiEnv, runtimeClientEnv, runtimeHeaders } from './runtime-auth.js';
import { TEST_TOKEN } from '../support/content.js';

// P6 end to end, black box: the studio deploys a bank's distribution to the runtime, and a rule value changes with no
// deployment. Values reach the runtime only in signed configuration releases (R-09-110), put in force with consent or, as here,
// in a test environment where the pipeline may (R-09-111).
//   1. The studio builds the bank's first distribution (Temporal, with simulators: a test one); the runtime's release API (on
//      PostgreSQL) installs it from GitHub, verified, and activates it for test.
//   2. The bank publishes its internal limit (1200 for 2026) in the studio; the approval builds a signed configuration release,
//      checked against the distribution's parameter contract; the runtime installs it and its values are in force.
//   3. A salary file of 1500 on 2026-11-17 runs on Temporal from the pinned distribution: its workers read the bank's values from
//      the runtime at each attempt, so the total is above the limit and the payment is held for approval.
//   4. The bank publishes 2000 from 2026-11-20: a new configuration release, a new value version; no new distribution, no new
//      activation.
//   5. A second salary file on 2026-11-24 runs from the same distribution: 1500 is within 2000, so it is not held. A file on the
//      first payment's date still takes 1200 and is held, as the first was: nothing was redeployed.
// Signed all the way (R-09-23 to R-09-27, R-09-49, R-09-51): the runtime verifies each bundle before it stores it, and the front
// door verifies the distribution it fetches from the runtime before a worker starts.

const REPO = path.resolve(STUDIO, '..');
const RUNTIME = path.join(REPO, 'runtime');
const TENANT = 'example-bank';
const ENV = 'test';
const MAKER = 'example.maker';
const PLATFORM = 'p.wf.corporate.v1';
const LIMIT = 'b.rule.internal-limit.v1#internalLimit';
const TWO_LINES = fs.readFileSync(path.join(STUDIO, 'fixtures', 'samples', 'wps-sif-two-lines.sif'), 'utf8'); // 1000 and 500, total 1500
const BENCH = [
  ...['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
  { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'approve' },
];
const PASSWORD = 'Th-Test-Only-1234';
const DOTNET_ENV = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };

let server: Server;
let api: ReturnType<typeof client>;
let runtime: ChildProcess;
let rt: RuntimeClient;
let pgPort = '';
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-p6-'));
const cache = path.join(work, 'distributions');
const containers: string[] = [];
let shipped: any;
let distribution = '';
const payments: Record<string, { trace: string[]; outcome?: string; distribution: string }> = {};

const freePort = () => new Promise<number>((resolve) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as any).port; s.close(() => resolve(p)); }); });
const build = (dir: string) => {
  const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: dir, encoding: 'utf8', env: DOTNET_ENV });
  if (b.status !== 0) throw new Error(`dotnet build ${dir} failed:\n${b.stdout}\n${b.stderr}`);
};
const store = () => `Host=127.0.0.1;Port=${pgPort};Username=postgres;Password=${PASSWORD};Database=th_p6`;
const call = async (method: string, p: string, body?: any) => {
  const r = await fetch(`${rt.url}${p}`, { method, headers: runtimeHeaders(body ? { 'content-type': 'application/json' } : {}), body: body ? JSON.stringify(body) : undefined });
  const t = await r.text();
  return { status: r.status, data: t ? JSON.parse(t) : undefined };
};
const install = async (tag: string) => {
  const r = await call('POST', '/sources/install', { tag });
  expect([200, 201], `${tag}: ${JSON.stringify(r.data)}`).toContain(r.status);
  return r.data;
};
const valueAt = async (t: string) => ((await call('GET', `/values?tenant=${TENANT}&at=${t}`)).data as any[]).find((v: any) => v.parameter === LIMIT)?.value;

beforeAll(async () => {
  build(path.join(RUNTIME, 'src', 'TransactHub.Api'));
  build(path.join(RUNTIME, 'tools', 'TransactHub.TemporalHarness'));
  const tts = path.join(RUNTIME, 'tools', 'temporal-test-server');
  if (!fs.existsSync(path.join(tts, 'classpath.txt'))) spawnSync(path.join(tts, 'setup.sh'), { encoding: 'utf8' });
  server = await startServer();
  api = client(server.url);
  // On approval, the studio builds the bank's distributions and configuration releases, for the test environment (with simulators).
  const c = await api.put('/api/git', { actor: 'shared.checker', target: 'temporal', distributions: true, testDistributions: true });
  expect(c.status, JSON.stringify(c.data)).toBe(200);
  shipped = (await api.get(`/api/tenants/${TENANT}/configuration`)).data.configurationSet;

  const name = `th-p6-pg-${process.pid}`;
  const pg = spawnSync('docker', ['run', '-d', '--name', name, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine'], { encoding: 'utf8' });
  if (pg.status !== 0) throw new Error(pg.stderr);
  containers.push(name);
  pgPort = spawnSync('docker', ['port', name, '5432'], { encoding: 'utf8' }).stdout.split('\n')[0].split(':').pop()!.trim();
  for (let i = 0; i < 60 && spawnSync('docker', ['exec', name, 'pg_isready', '-U', 'postgres']).status !== 0; i++) await new Promise((r) => setTimeout(r, 1000));
  const url = `http://127.0.0.1:${await freePort()}`;
  runtime = spawn('dotnet', [path.join(RUNTIME, 'src', 'TransactHub.Api', 'bin', 'Release', 'net10.0', 'TransactHub.Api.dll'), '--urls', url], {
    env: {
      ...DOTNET_ENV, ...runtimeApiEnv(), Store__Provider: 'PostgreSql', Store__Connection: store(),
      PackageSource__GitHub__Repository: server.repo, PackageSource__GitHub__Api: server.fake.url, PackageSource__GitHub__TokenRef: 'env:TH_BB_GH_READ_TOKEN', TH_BB_GH_READ_TOKEN: TEST_TOKEN,
    }, stdio: 'ignore',
  });
  for (let i = 0; i < 120; i++) { try { if ((await fetch(`${url}/health/ready`)).ok) break; } catch { /* not yet */ } await new Promise((r) => setTimeout(r, 500)); }
  rt = { url, headers: runtimeHeaders() };
  // The workers read the bank's values from the runtime at each attempt (TH_RUNTIME_URL, its token by reference).
  Object.assign(process.env, runtimeClientEnv(), { TH_RUNTIME_URL: url, TH_STORE_PROVIDER: 'PostgreSql', TH_STORE_CONNECTION: store() });
}, 900000);
afterAll(() => {
  server?.stop();
  runtime?.kill();
  for (const k of ['TH_RUNTIME_URL', 'TH_STORE_PROVIDER', 'TH_STORE_CONNECTION']) delete process.env[k];
  for (const c of containers) spawnSync('docker', ['rm', '-f', '-v', c]);
  fs.rmSync(work, { recursive: true, force: true });
});

/**
 * A new release of the bank's configuration set, saved by the bank's designer and approved by the bank approver: one commit on
 * the branch, and the signed configuration release the approval builds. The limit's 2026 period gets the value; from a later
 * validFrom, the earlier period is closed the day before (periods may not overlap, L14).
 */
async function publishLimit(value: number, validFrom?: string) {
  const c = JSON.parse(JSON.stringify(shipped));
  // The next minor of the release on the branch (R-06-15): the bank's configuration set has releases of its own before this test.
  const [major, minor] = String(shipped.release).split('.').map(Number);
  const version = `${major}.${minor + 1}.0`;
  c.release = version;
  const year = c.values.filter((v: any) => v.parameter === LIMIT && v.validTo === '2026-12-31');
  const last = year[year.length - 1];
  if (!validFrom) last.value = value;
  else {
    const before = new Date(Date.parse(`${validFrom}T00:00:00Z`) - 86400000).toISOString().slice(0, 10);
    c.values.splice(c.values.indexOf(last) + 1, 0, { parameter: LIMIT, value, validFrom, validTo: '2026-12-31' });
    last.validTo = before;
  }
  const saved = await api.post(`/api/tenants/${TENANT}/configuration`, { actor: MAKER, yaml: toYaml(c) });
  expect(saved.status, JSON.stringify(saved.data)).toBe(200);
  const approved = await api.post('/api/publish', { tenant: TENANT, key: saved.data.draft.key, checker: 'example.checker' });
  expect(approved.status, JSON.stringify(approved.data).slice(0, 400)).toBe(200);
  shipped = c;
  const bundle = (approved.data.bundles as any[]).find((b) => b.kind === 'configuration');
  expect(bundle, JSON.stringify(approved.data.bundles)).toBeDefined();
  expect(bundle.releaseId).toBe(`config/${TENANT}@${version}`);
  return bundle as { releaseId: string; tag: string };
}

/** A salary file on a date: accepted at the front door, pinned to the active distribution, run on Temporal from it. */
let n = 0;
async function pay(label: string, date: string) {
  const paymentId = `PAY-P6-${process.pid}-${++n}`;
  const pinned = await accept(rt, { paymentId, bank: TENANT, environment: ENV });
  const r = await runPinned({
    runtime: rt, bank: TENANT, environment: ENV, paymentId, distribution: pinned, workflow: PLATFORM, studioRoot: STUDIO, cache,
    inbound: { message: 'uae.wps.sif', payload: TWO_LINES }, deployEnvironment: { evaluationDate: date, bench: BENCH },
  });
  const p = { trace: r.result.trace, outcome: r.result.outcome, distribution: pinned };
  payments[label] = p;
  expect(r.result.log.join('\n')).toContain(`values from the runtime at ${rt.url}`);
  // The front door verified the distribution the runtime gave it, with the studio's key, before anything ran.
  expect(r.result.verification.keyId).toMatch(/^[0-9a-f]{32}$/);
  expect(r.result.workers.some((w) => (w.valuesRead ?? 0) > 0), JSON.stringify(r.result.workers)).toBe(true);
  expect(r.ended).toBe(true);
  return { ...p, log: r.result.log };
}

describe('P6: the studio deploys a distribution to the runtime and hot-updates a rule value with no deployment', () => {
  it('1. the bank\'s first distribution is installed from GitHub, verified, and activated for test', async () => {
    const d = await api.post('/api/distributions', { actor: 'example.checker', bank: TENANT, engine: 'temporal', withSimulators: true });
    expect(d.status, JSON.stringify(d.data).slice(0, 600)).toBe(200);
    // Without a token the runtime takes nothing.
    expect((await fetch(`${rt.url}/sources/install`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ tag: d.data.tag }) })).status).toBe(401);
    const inst = await install(d.data.tag);
    expect(inst).toMatchObject({ state: 'installed', signedBy: expect.stringMatching(/^[0-9a-f]{32}$/) });
    distribution = d.data.releaseId;
    expect((await call('POST', '/distributions/activate', { id: distribution, environment: ENV, why: 'P6' })).status).toBe(201);
    // No value of the bank is in force yet: values arrive only in a configuration release.
    expect(await valueAt('2026-11-17T06:00:00Z')).toBeUndefined();
  }, 600000);

  it('2. the bank publishes its internal limit; its signed configuration release is installed and its values are in force', async () => {
    const c = await publishLimit(1200);
    const inst = await install(c.tag);
    expect(inst).toMatchObject({ inForce: true, state: 'in-force', checkedAgainst: [distribution] });
    expect(await valueAt('2026-11-17T06:00:00Z')).toBe(1200);
  }, 300000);

  it('3. a salary file of 1500 on 2026-11-17 is held: the total is above the limit the runtime holds for that date', async () => {
    const a = await pay('first', '2026-11-17');
    expect(a.outcome, a.log.slice(-40).join('\n')).toBe('settled');
    expect(a.trace).toContain('hook:hold');
    expect(a.trace).toContain('hold:approved');
    expect(a.distribution).toBe(distribution);
  }, 600000);

  it('4. the bank raises the limit to 2000 from 2026-11-20: a configuration release, a new value version, no new distribution or activation', async () => {
    const before = ((await call('GET', `/distributions?bank=${TENANT}&environment=${ENV}`)).data as any[]).length;
    const c = await publishLimit(2000, '2026-11-20');
    expect((await install(c.tag)).inForce).toBe(true);
    expect(await valueAt('2026-11-24T06:00:00Z')).toBe(2000);
    expect(await valueAt('2026-11-17T06:00:00Z')).toBe(1200);
    expect(((await call('GET', `/distributions?bank=${TENANT}&environment=${ENV}`)).data as any[]).length).toBe(before);
    expect((await call('GET', `/distributions/active?bank=${TENANT}&environment=${ENV}`)).data.distribution).toBe(distribution);
  }, 300000);

  it('5. with nothing redeployed, a salary file on 2026-11-24 takes the new limit and is not held; the first payment keeps its own', async () => {
    const b = await pay('second', '2026-11-24');
    expect(b.outcome, b.log.slice(-40).join('\n')).toBe('settled');
    expect(b.trace).not.toContain('hook:hold');
    expect(b.distribution).toBe(payments.first.distribution);
    // A payment on the first payment's date still takes 1200: the first ran on the value of its date, and still would.
    const c = await pay('first-date-again', '2026-11-17');
    expect(c.trace).toEqual(payments.first.trace);
    expect(c.distribution).toBe(distribution);
  }, 900000);
});
