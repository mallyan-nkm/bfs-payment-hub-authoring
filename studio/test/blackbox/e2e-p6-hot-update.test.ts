import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as crypto from 'node:crypto';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { toYaml } from '../../src/core/yaml.js';
import { client, startServer, STUDIO, type Server } from './server.js';

// P6 end to end, black box: the studio deploys a release to the runtime, and a rule value changes with no deployment.
//   1. The bank publishes its internal limit (1200 for 2026) in the studio; the change lands on the connected branch.
//   2. The corporate workflow's Temporal package is stored by the runtime's release API (PostgreSQL) and activated for test;
//      the pipeline imports the branch, so the runtime holds the bank's values as effective-dated versions.
//   3. A salary file of 1500 on 2026-11-17 runs on Temporal from the stored package: its workers read the bank's values from
//      the runtime at each attempt, so the total is above the limit and the payment is held for approval.
//   4. The bank publishes 2000 from 2026-11-20. The pipeline imports the branch again: a new value version, no new release,
//      no new activation.
//   5. A second salary file on 2026-11-24 runs from the same stored package, byte for byte: 1500 is within 2000, so it is not
//      held. A file on the first payment's date still takes 1200 and is held, as the first was: the first payment keeps
//      the value of its date and the release it ran, and nothing was redeployed.

const REPO = path.resolve(STUDIO, '..');
const RUNTIME = path.join(REPO, 'runtime');
const TENANT = 'example-bank';
const MAKER = 'example.maker';
const PLATFORM = 'p.wf.corporate.v1';
const SCOPE = `${PLATFORM}/temporal`;
const LIMIT = 'b.rule.internal-limit.v1#internalLimit';
const TWO_LINES = path.join(STUDIO, 'fixtures', 'samples', 'wps-sif-two-lines.sif'); // 1000 and 500, total 1500
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
let runtimeUrl = '';
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-p6-'));
const containers: string[] = [];
let shipped: any;
let release = '';
const payments: Record<string, { trace: string[]; outcome?: string; sha: string; out: string }> = {};

const freePort = () => new Promise<number>((resolve) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as any).port; s.close(() => resolve(p)); }); });
const build = (dir: string) => {
  const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: dir, encoding: 'utf8', env: DOTNET_ENV });
  if (b.status !== 0) throw new Error(`dotnet build ${dir} failed:\n${b.stdout}\n${b.stderr}`);
};
const post = async (url: string, body: any) => (await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) }));

beforeAll(async () => {
  build(path.join(RUNTIME, 'src', 'TransactHub.Api'));
  build(path.join(RUNTIME, 'tools', 'TransactHub.TemporalHarness'));
  const tts = path.join(RUNTIME, 'tools', 'temporal-test-server');
  if (!fs.existsSync(path.join(tts, 'classpath.txt'))) spawnSync(path.join(tts, 'setup.sh'), { encoding: 'utf8' });
  server = await startServer();
  api = client(server.url);
  const c = await api.put('/api/git', { actor: 'shared.checker', target: 'temporal', environment: 'test' });
  expect(c.status).toBe(200);
  shipped = (await api.get(`/api/tenants/${TENANT}/configuration`)).data.configurationSet;

  const name = `th-p6-pg-${process.pid}`;
  const pg = spawnSync('docker', ['run', '-d', '--name', name, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine'], { encoding: 'utf8' });
  if (pg.status !== 0) throw new Error(pg.stderr);
  containers.push(name);
  const port = spawnSync('docker', ['port', name], { encoding: 'utf8' }).stdout.split('\n')[0].split(':').pop()!.trim();
  for (let i = 0; i < 60 && spawnSync('docker', ['exec', name, 'pg_isready', '-U', 'postgres']).status !== 0; i++) await new Promise((r) => setTimeout(r, 1000));
  runtimeUrl = `http://127.0.0.1:${await freePort()}`;
  runtime = spawn('dotnet', [path.join(RUNTIME, 'src', 'TransactHub.Api', 'bin', 'Release', 'net10.0', 'TransactHub.Api.dll'), '--urls', runtimeUrl], {
    env: { ...DOTNET_ENV, Store__Provider: 'PostgreSql', Store__Connection: `Host=127.0.0.1;Port=${port};Username=postgres;Password=${PASSWORD};Database=th_p6` }, stdio: 'ignore',
  });
  for (let i = 0; i < 120; i++) { try { await fetch(`${runtimeUrl}/activations/active?environment=x&scope=x`); break; } catch { await new Promise((r) => setTimeout(r, 500)); } }
}, 900000);
afterAll(() => {
  server?.stop();
  runtime?.kill();
  for (const c of containers) spawnSync('docker', ['rm', '-f', c]);
  fs.rmSync(work, { recursive: true, force: true });
});

/**
 * A new release of the bank's configuration set, saved by the bank's designer: one commit on the branch. The limit's 2026
 * period gets the value; from a later validFrom, the earlier period is closed the day before (periods may not overlap, L14).
 */
async function publishLimit(version: string, value: number, validFrom?: string) {
  const c = JSON.parse(JSON.stringify(shipped));
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
  // The bank approver makes it the version the runtime is given (R-11-31).
  const approved = await api.post('/api/publish', { tenant: TENANT, key: saved.data.draft.key, checker: 'example.checker' });
  expect(approved.status, JSON.stringify(approved.data).slice(0, 400)).toBe(200);
  shipped = c;
  const onBranch = server.fake.files(server.repo, 'main')[`content-configurations/banks/${TENANT}/configuration-sets/${version}.yaml`];
  expect(onBranch, 'the configuration set is on the branch').toContain(`value: ${value}`);
}

/** The pipeline on the connected branch: check it out and post it to the runtime (plan W8). */
async function pipeline() {
  const look = fs.mkdtempSync(path.join(work, 'branch-'));
  for (const [p, text] of Object.entries(server.fake.files(server.repo, 'main'))) {
    if (!p.startsWith('content-configurations/')) continue;
    const f = path.join(look, p.slice('content-configurations/'.length));
    fs.mkdirSync(path.dirname(f), { recursive: true });
    fs.writeFileSync(f, text);
  }
  const r = await (await post(`${runtimeUrl}/import-branch`, { path: look, actor: 'pipeline' })).json();
  expect(r.problems, JSON.stringify(r.problems)).toEqual([]);
  return r;
}

/** A salary file on a date, run on Temporal from the package the runtime holds for the active release. */
async function pay(label: string, date: string) {
  const active = await (await fetch(`${runtimeUrl}/activations/active?environment=test&scope=${encodeURIComponent(SCOPE)}`)).json();
  expect(active.release).toBe(release);
  const zip = Buffer.from(await (await fetch(`${runtimeUrl}/releases/package?scope=${encodeURIComponent(SCOPE)}&release=${active.release}`)).arrayBuffer());
  const file = path.join(work, `${label}.zip`);
  fs.writeFileSync(file, zip);
  const args = ['src/cli.ts', 'deploy', file, '--workflow', PLATFORM, '--message', 'uae.wps.sif', '--inbound', TWO_LINES, '--tenant', TENANT, '--date', date, ...BENCH.flatMap((b) => ['--bench', `${b.port}=${b.simulator}:${b.scenario}`])];
  const out = await new Promise<string>((resolve) => {
    let text = '';
    const child = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), args, { cwd: STUDIO, env: { ...process.env, TH_RUNTIME_URL: runtimeUrl } });
    const timer = setTimeout(() => child.kill('SIGKILL'), 600000);
    child.stdout.on('data', (d) => (text += d));
    child.stderr.on('data', (d) => (text += d));
    child.on('close', () => { clearTimeout(timer); resolve(text); });
  });
  const p = { out, trace: /^trace  (.*)$/m.exec(out)?.[1].split(' | ') ?? [], outcome: /^outcome (\w+)/m.exec(out)?.[1], sha: crypto.createHash('sha256').update(zip).digest('hex') };
  payments[label] = p;
  expect(out).toContain(`values from the runtime at ${runtimeUrl}`);
  expect(out, out.slice(-2000)).toMatch(/worker th-bank .*values read from the runtime [1-9]\d* time/);
  return p;
}

describe('P6: the studio deploys a release to the runtime and hot-updates a rule value with no deployment', () => {
  it('1. the bank publishes its internal limit in the studio, and the change lands on the connected branch', async () => {
    await publishLimit('1.1.0', 1200);
  }, 120000);

  it('2. the runtime stores the Temporal package once, activates it for test, and takes the bank\'s values from the branch', async () => {
    const z = await api.get(`/api/export/download?workflows=${PLATFORM}&target=temporal&withSimulators=true&tenant=${TENANT}`);
    expect(z.status).toBe(200);
    const stored = await (await fetch(`${runtimeUrl}/releases?actor=pipeline`, { method: 'POST', body: z.data })).json();
    expect(stored, JSON.stringify(stored)).toMatchObject({ scope: SCOPE, stored: true });
    release = stored.release;
    expect((await post(`${runtimeUrl}/activations`, { environment: 'test', scope: SCOPE, release, actor: 'ops' })).status).toBe(201);
    const imported = await pipeline();
    expect(imported.values).toBeGreaterThan(0);
    const inForce = await (await fetch(`${runtimeUrl}/values?tenant=${TENANT}&at=2026-11-17T06:00:00Z`)).json();
    expect(inForce.find((v: any) => v.parameter === LIMIT)?.value).toBe(1200);
  }, 300000);

  it('3. a salary file of 1500 on 2026-11-17 is held: the total is above the limit the runtime holds for that date', async () => {
    const a = await pay('first', '2026-11-17');
    expect(a.outcome, a.out.slice(-2500)).toBe('settled');
    expect(a.trace).toContain('hook:hold');
    expect(a.trace).toContain('hold:approved');
  }, 600000);

  it('4. the bank raises the limit to 2000 from 2026-11-20; the runtime takes it as a new value version, with no new release or activation', async () => {
    await publishLimit('1.2.0', 2000, '2026-11-20');
    const imported = await pipeline();
    expect(imported.values).toBeGreaterThan(0);
    expect(imported.stored).toEqual([]);
    expect(imported.activated).toEqual([]);
    const at = async (t: string) => (await (await fetch(`${runtimeUrl}/values?tenant=${TENANT}&at=${t}`)).json()).find((v: any) => v.parameter === LIMIT)?.value;
    expect(await at('2026-11-24T06:00:00Z')).toBe(2000);
    expect(await at('2026-11-17T06:00:00Z')).toBe(1200);
  }, 300000);

  it('5. with nothing redeployed, a salary file on 2026-11-24 takes the new limit and is not held; the first payment keeps its own', async () => {
    const b = await pay('second', '2026-11-24');
    expect(b.outcome, b.out.slice(-2500)).toBe('settled');
    expect(b.trace).not.toContain('hook:hold');
    // The same package, byte for byte, and the same release: no deployment came between the two payments.
    expect(b.sha).toBe(payments.first.sha);
    // A payment on the first payment's date still takes 1200: the first ran on the value of its date, and still would.
    const c = await pay('first-date-again', '2026-11-17');
    expect(c.trace).toEqual(payments.first.trace);
    expect(c.sha).toBe(payments.first.sha);
  }, 900000);
});
