import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { parseYaml } from '../../src/core/yaml.js';
import { accept, distributionCopy, runPinned, type RuntimeClient } from '../../src/deploy/front-door.js';
import { startWorkers, type WorkerSet } from '../../src/deploy/deploy.js';
import { client, startServer, STUDIO, type Server } from './server.js';
import { runtimeApiEnv, runtimeHeaders } from './runtime-auth.js';
import { TEST_TOKEN } from '../support/content.js';
import { wpsCopy } from './native-module.js';

// Native code in the distribution (docs/runtime/PACKAGING-AND-DEPLOYMENT.md sections 3, 6 and scenario 4; spec R-09-120 to
// R-09-124), end to end, black box. The studio builds each distribution with the WPS native assembly built from its code-behind
// project at publish (TH_NATIVE_MODULES=build), its SHA-256 in the signed bill of materials. Two scheme patch releases of the same
// WPS native activity (the ACK mapping, which writes a marker of its build) give distributions A and B; the runtime's release API
// (on PostgreSQL) verifies and installs each; the native host, whose image carries no scheme code, loads each distribution's
// assembly from the store into a context of its own. A payment pinned to A runs A's build while one pinned to B runs B's, on
// Temporal and on DTFx, both through one set of workers that names no distribution: each attempt names its payment's
// distribution, from the engine to the worker to the native host (R-09-130 to R-09-133, Issue I-222). The host refuses an
// attempt whose worker runs another distribution, and one for a distribution the payment is not pinned to. Removal and unloading are shown by runtime/tests/TransactHub.Native.Tests (SideBySideTests): here a
// removal would wait for the distribution's longest declared wait (P30D).

const REPO = path.resolve(STUDIO, '..');
const RUNTIME = path.join(REPO, 'runtime');
const BANK = 'example-bank';
const ENV = 'test';
const PLATFORM = 'p.wf.corporate.v1';
const ACK = 'uae.wps.map.ack-in.v1';
const BENCH = [
  ...['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
];
const SIF = fs.readFileSync(path.join(STUDIO, 'fixtures', 'samples', 'wps-sif-valid.sif'), 'utf8');
const PASSWORD = 'Th-Test-Only-1234';
const DOTNET_ENV = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };
const EVALUATION_DATE = '2026-11-17';
const sets: WorkerSet[] = [];

let server: Server;
let api: ReturnType<typeof client>;
let runtime: ChildProcess | undefined;
let native: ChildProcess | undefined;
let nativeUrl = '';
let rt: RuntimeClient;
let pgPort = '';
let configSet: any;
let mark: (m: string) => void;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-native-'));
const cache = path.join(work, 'distributions');
const workerLog = path.join(work, 'worker.log');
const containers: string[] = [];
const tag = `${process.pid}-${Date.now() % 100000}`;
const saved: Record<string, string | undefined> = {};

const freePort = () => new Promise<number>((resolve) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as any).port; s.close(() => resolve(p)); }); });
const build = (dir: string) => {
  const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: dir, encoding: 'utf8', env: DOTNET_ENV });
  if (b.status !== 0) throw new Error(`dotnet build ${dir} failed:\n${b.stdout}\n${b.stderr}`);
};
const store = () => `Host=127.0.0.1;Port=${pgPort};Username=postgres;Password=${PASSWORD};Database=th_native`;
const waitFor = async (url: string) => { for (let i = 0; i < 240; i++) { try { if ((await fetch(url)).ok) return; } catch { /* not yet */ } await new Promise((r) => setTimeout(r, 500)); } throw new Error(`${url} did not come up`); };
const setEnv = (k: string, v: string) => { if (!(k in saved)) saved[k] = process.env[k]; process.env[k] = v; };

const call = async (method: string, p: string, body?: any) => {
  const r = await fetch(`${rt.url}${p}`, { method, headers: runtimeHeaders(body ? { 'content-type': 'application/json' } : {}), body: body ? JSON.stringify(body) : undefined });
  const t = await r.text();
  return { status: r.status, data: t ? JSON.parse(t) : undefined };
};
const manifestOf = (id: string) => parseYaml(server.fake.files(server.repo, 'main')[`content-configurations/releases/distributions/${BANK}/${id.split('@')[1]}/manifest.yaml`]);

/** A distribution of the bank for an engine, with the WPS build the copy's sources give now: published, installed, activated. */
async function distribution(engine: string, marker: string) {
  mark(marker);
  const d = await api.post('/api/distributions', { actor: 'example.checker', bank: BANK, engine, withSimulators: true });
  expect(d.status, JSON.stringify(d.data).slice(0, 800)).toBe(200);
  const inst = await call('POST', '/sources/install', { tag: d.data.tag });
  expect([200, 201], JSON.stringify(inst.data)).toContain(inst.status);
  const act = await call('POST', '/distributions/activate', { id: d.data.releaseId, environment: ENV, why: 'e2e native' });
  expect(act.status, JSON.stringify(act.data)).toBe(201);
  return d.data.releaseId as string;
}

let n = 0;
const acceptPayment = async () => {
  const paymentId = `PAY-NAT-${tag}-${++n}`;
  return { paymentId, distribution: await accept(rt, { paymentId, bank: BANK, environment: ENV }) };
};

/**
 * One set of workers for the bank's distributions, as an installation runs them (R-09-133): started once from a distribution's
 * engine package, naming no distribution (no TH_DISTRIBUTION).
 */
async function sharedWorkers(from: string) {
  const { dir } = await distributionCopy(rt, from, cache);
  const set = await startWorkers({ packageDir: path.join(dir, 'engine', PLATFORM), studioRoot: STUDIO, environment: { tenant: BANK, evaluationDate: EVALUATION_DATE, bench: BENCH, configurationSet: configSet } });
  sets.push(set);
  return set;
}

/** Runs a pinned payment and returns what its ACK mapping wrote, from the workers' log of this run, and the workers it ran on. */
async function run(p: { paymentId: string; distribution: string }, engine: string, workers?: WorkerSet) {
  fs.writeFileSync(workerLog, '');
  const r = await runPinned({
    runtime: rt, bank: BANK, environment: ENV, paymentId: p.paymentId, distribution: p.distribution, workflow: PLATFORM, studioRoot: STUDIO, cache,
    inbound: { message: 'uae.wps.sif', payload: SIF }, deployEnvironment: { evaluationDate: EVALUATION_DATE, bench: BENCH, configurationSet: configSet }, engine: engine as any, workers,
  });
  expect(r.result.outcome, r.result.log.slice(-30).join('\n')).toBe('settled');
  expect(r.result.engine.distribution?.id).toBe(p.distribution);
  const acks = fs.readFileSync(workerLog, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l)).filter((e) => e.task?.startsWith(`${ACK}@`));
  expect(acks.length, 'the ACK mapping ran on the native host').toBeGreaterThan(0);
  return { builds: acks.map((e) => e.result?.writes?.status?.additionalInformation ?? e.result?.writes?.['status']?.additionalInformation), pids: r.result.workers.map((w) => w.pid).sort() };
}

/** An attempt of the ACK mapping as a worker sends it to the native host. */
const nativeAttempt = async (paymentId: string, distribution: string, workerDistribution?: string) => (await (await fetch(`${nativeUrl}/native`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ activity: ACK, release: '1.2.0', distribution, ...(workerDistribution ? { workerDistribution } : {}), now: `${EVALUATION_DATE}T08:00:00Z`, tenant: BANK, params: {}, view: { header: { scheme: 'uae.wps', paymentId }, messages: { lastReceived: { status: 'accepted' } } } }),
})).json()) as any;

const modules = async () => (await (await fetch(`${nativeUrl}/modules`)).json()) as any;

beforeAll(async () => {
  build(path.join(RUNTIME, 'src', 'TransactHub.Api'));
  build(path.join(RUNTIME, 'src', 'TransactHub.Native'));
  build(path.join(RUNTIME, 'tools', 'TransactHub.TemporalHarness'));
  const tts = path.join(RUNTIME, 'tools', 'temporal-test-server');
  if (!fs.existsSync(path.join(tts, 'classpath.txt'))) spawnSync(path.join(tts, 'setup.sh'), { encoding: 'utf8' });
  const name = `th-e2e-native-pg-${tag}`;
  const pg = spawnSync('docker', ['run', '-d', '--name', name, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine'], { encoding: 'utf8' });
  if (pg.status !== 0) throw new Error(pg.stderr);
  containers.push(name);
  pgPort = spawnSync('docker', ['port', name, '5432'], { encoding: 'utf8' }).stdout.split('\n')[0].split(':').pop()!.trim();
  for (let i = 0; i < 60 && spawnSync('docker', ['exec', name, 'pg_isready', '-U', 'postgres']).status !== 0; i++) await new Promise((r) => setTimeout(r, 1000));

  // The studio builds each distribution's WPS native assembly from the code-behind project: here a copy whose ACK mapping says
  // which build it is.
  const copy = wpsCopy(work, 'wps-build-a');
  mark = copy.mark;
  setEnv('TH_NATIVE_MODULES', 'build');
  setEnv('TH_NATIVE_PROJECTS', `uae.wps=${copy.dir}`);
  setEnv('TH_NATIVE_BUILD_CACHE', path.join(work, 'module-builds'));
  server = await startServer();
  api = client(server.url);

  // The release API on PostgreSQL, its package source the studio's content repository.
  const url = `http://127.0.0.1:${await freePort()}`;
  runtime = spawn('dotnet', [path.join(RUNTIME, 'src', 'TransactHub.Api', 'bin', 'Release', 'net10.0', 'TransactHub.Api.dll'), '--urls', url], {
    env: {
      ...DOTNET_ENV, ...runtimeApiEnv(),
      PackageSource__GitHub__Repository: server.repo, PackageSource__GitHub__Api: server.fake.url, PackageSource__GitHub__TokenRef: 'env:TH_BB_GH_READ_TOKEN', TH_BB_GH_READ_TOKEN: TEST_TOKEN,
      Store__Provider: 'PostgreSql', Store__Connection: store(),
    }, stdio: 'ignore',
  });
  await waitFor(`${url}/health/ready`);
  rt = { url, headers: runtimeHeaders() };

  // The native host on the same store, with no development modules: WPS reaches it only in a distribution.
  nativeUrl = `http://127.0.0.1:${await freePort()}`;
  native = spawn('dotnet', [path.join(RUNTIME, 'src', 'TransactHub.Native', 'bin', 'Release', 'net10.0', 'TransactHub.Native.dll')], {
    env: { ...DOTNET_ENV, TH_NATIVE_URLS: nativeUrl, TH_STORE_PROVIDER: 'PostgreSql', TH_STORE_CONNECTION: store(), TH_NATIVE_REFRESH: '00:00:02', TH_NATIVE_DEV_MODULES: '' }, stdio: 'ignore',
  });
  await waitFor(`${nativeUrl}/health`);

  // The deployed workers send the ACK mapping to the native host, each naming its distribution (TH_DISTRIBUTION).
  setEnv('TH_NATIVE_HOST', nativeUrl);
  setEnv('TH_NATIVE_ACTIVITIES', ACK);
  setEnv('TH_WORKER_LOG', workerLog);
  setEnv('TH_STORE_PROVIDER', 'PostgreSql');
  setEnv('TH_STORE_CONNECTION', store());
  configSet = parseYaml(fs.readFileSync(path.join(REPO, 'content-configurations', 'banks', BANK, 'configuration-set.yaml'), 'utf8'));
  const s = await api.put('/api/git', { actor: 'shared.checker', target: 'temporal', distributions: true, testDistributions: true });
  expect(s.status, JSON.stringify(s.data)).toBe(200);
}, 1800000);

afterAll(async () => {
  for (const s of sets) await s.stop();
  server?.stop();
  runtime?.kill();
  native?.kill();
  for (const [k, v] of Object.entries(saved)) { if (v === undefined) delete process.env[k]; else process.env[k] = v; }
  for (const c of containers) spawnSync('docker', ['rm', '-f', c]);
  fs.rmSync(work, { recursive: true, force: true });
});

describe.each(['temporal', 'dtfx'] as const)('two builds of a WPS native activity, side by side, on %s', (engine) => {
  it('a payment pinned to A runs A\'s build while a payment pinned to B runs B\'s', async () => {
    // The image carries no WPS code.
    const health = await (await fetch(`${nativeUrl}/health`)).json() as any;
    expect(health.activities).not.toContain(ACK);

    const a = await distribution(engine, 'wps-build-a');
    const pa = await acceptPayment();
    expect(pa.distribution).toBe(a);
    // A scheme patch release of the activity's code-behind, then a new distribution; A drains.
    const b = await distribution(engine, 'wps-build-b');
    const pb = await acceptPayment();
    expect(pb.distribution).toBe(b);

    // Each carries its own build, its SHA-256 in the signed bill of materials.
    const [ma, mb] = [manifestOf(a), manifestOf(b)];
    const shaOf = (m: any) => m.native.modules.find((x: any) => x.scope === 'uae.wps').files[0].sha256;
    expect(ma.native.modules[0].activities).toContain(`${ACK}@1.2.0`);
    expect(shaOf(ma)).not.toBe(shaOf(mb));
    expect(mb.billOfMaterials.artefacts.find((x: any) => x.kind === 'native-assembly').assemblySha256).toBe(shaOf(mb));

    // One set of workers, started once and naming no distribution, runs both: B's payment first, then A's. Each attempt names
    // its payment's distribution, and each payment runs its own distribution's build (R-09-133).
    const shared = await sharedWorkers(a);
    const rb = await run(pb, engine, shared);
    expect(new Set(rb.builds)).toEqual(new Set(['wps-build-b']));
    const ra = await run(pa, engine, shared);
    expect(new Set(ra.builds)).toEqual(new Set(['wps-build-a']));
    expect(ra.pids).toEqual(rb.pids);
    expect(ra.pids.every((pid) => pid > 0)).toBe(true);

    // A worker that runs A (TH_DISTRIBUTION) does not serve B's payment (R-09-131), and no attempt runs a distribution its payment is
    // not pinned to (R-09-132); the attempt for the payment's own distribution is served.
    const wrong = await nativeAttempt(pb.paymentId, b, a);
    expect([wrong.kind, wrong.name], JSON.stringify(wrong)).toEqual(['error', 'WrongDistribution']);
    const notPinned = await nativeAttempt(pb.paymentId, a);
    expect([notPinned.kind, notPinned.name], JSON.stringify(notPinned)).toEqual(['error', 'NotPinned']);
    const right = await nativeAttempt(pb.paymentId, b, b);
    expect(right.writes?.status?.additionalInformation, JSON.stringify(right)).toBe('wps-build-b');

    // Both loaded side by side in the one host, each in a collectible context of its own, with its own hash.
    const m = await modules();
    const la = m.loaded.find((x: any) => x.distribution === a);
    const lb = m.loaded.find((x: any) => x.distribution === b);
    expect(la.collectible && lb.collectible).toBe(true);
    expect(la.context).not.toBe(lb.context);
    expect(la.modules[0].sha256).toBe(shaOf(ma));
    expect(lb.modules[0].sha256).toBe(shaOf(mb));
    expect(la.served[ACK]).toBeGreaterThan(0);
    expect(lb.served[ACK]).toBeGreaterThan(0);
  }, 3600000);
});
