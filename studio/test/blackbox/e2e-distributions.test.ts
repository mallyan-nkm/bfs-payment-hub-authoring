import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { parseYaml, toYaml } from '../../src/core/yaml.js';
import { accept, runPinned, type RuntimeClient } from '../../src/deploy/front-door.js';
import { client, startServer, STUDIO, type Server } from './server.js';
import { runtimeApiEnv, runtimeHeaders } from './runtime-auth.js';
import { TEST_TOKEN } from '../support/content.js';

// The packaging and deployment design (docs/runtime/PACKAGING-AND-DEPLOYMENT.md, section 8), end to end, black box: the
// studio builds a bank's distributions and configuration releases on approval, signs them and publishes them with their
// tags to the content branch (the fake GitHub); the runtime's release API (on PostgreSQL) lists them from GitHub, verifies
// and installs them, activates them per bank and environment, pins each payment at acceptance and records its end; the
// front door runs each payment from the distribution it is pinned to, on Temporal (and, for scenarios 3, 4, 5, 8 and 9, on
// DTFx, Elsa and Flowable), each distribution its own copy beside the others.

const REPO = path.resolve(STUDIO, '..');
const RUNTIME = path.join(REPO, 'runtime');
const BANK = 'example-bank';
const ENV = 'test';
const PLATFORM = 'p.wf.corporate.v1';
const BENCH = [
  ...['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
];
const SIF = fs.readFileSync(path.join(STUDIO, 'fixtures', 'samples', 'wps-sif-valid.sif'), 'utf8');
const PASSWORD = 'Th-Test-Only-1234';
const DOTNET_ENV = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };
const FLOWABLE_IMAGE = 'flowable/flowable-rest:7.1.0@sha256:b7ae287502cd5c243b577a0ca2894172e1c18cd75aea4fd67dc7d58f1b40db44';

let server: Server;
let api: ReturnType<typeof client>;
let runtime: ChildProcess | undefined;
let rt: RuntimeClient;
let pgPort = '';
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-dist-'));
const cache = path.join(work, 'distributions');
const containers: string[] = [];
const tag = `${process.pid}-${Date.now() % 100000}`;
let configSet: any;

const freePort = () => new Promise<number>((resolve) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as any).port; s.close(() => resolve(p)); }); });
const build = (dir: string) => {
  const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: dir, encoding: 'utf8', env: DOTNET_ENV });
  if (b.status !== 0) throw new Error(`dotnet build ${dir} failed:\n${b.stdout}\n${b.stderr}`);
};
const store = () => `Host=127.0.0.1;Port=${pgPort};Username=postgres;Password=${PASSWORD};Database=th_dist`;

/** The runtime's release API on the PostgreSQL store, with the studio's content repository as its package source. */
async function startRuntime() {
  const url = `http://127.0.0.1:${await freePort()}`;
  runtime = spawn('dotnet', [path.join(RUNTIME, 'src', 'TransactHub.Api', 'bin', 'Release', 'net10.0', 'TransactHub.Api.dll'), '--urls', url], {
    env: {
      ...DOTNET_ENV, ...runtimeApiEnv(),
      PackageSource__GitHub__Repository: server.repo, PackageSource__GitHub__Api: server.fake.url, PackageSource__GitHub__TokenRef: 'env:TH_BB_GH_READ_TOKEN', TH_BB_GH_READ_TOKEN: TEST_TOKEN,
      Store__Provider: 'PostgreSql', Store__Connection: store(),
    }, stdio: 'ignore',
  });
  for (let i = 0; i < 120; i++) { try { if ((await fetch(`${url}/health/ready`)).ok) break; } catch { /* not yet */ } await new Promise((r) => setTimeout(r, 500)); }
  rt = { url, headers: runtimeHeaders() };
}

const call = async (method: string, p: string, body?: any) => {
  const r = await fetch(`${rt.url}${p}`, { method, headers: runtimeHeaders(body ? { 'content-type': 'application/json' } : {}), body: body ? JSON.stringify(body) : undefined });
  const t = await r.text();
  return { status: r.status, data: t ? JSON.parse(t) : undefined };
};
const install = async (t: string) => {
  const r = await call('POST', '/sources/install', { tag: t });
  expect([200, 201], `${t}: ${JSON.stringify(r.data)}`).toContain(r.status);
  return r.data;
};
const activate = async (id: string, environment = ENV) => {
  const r = await call('POST', '/distributions/activate', { id, environment, why: 'e2e' });
  expect(r.status, JSON.stringify(r.data)).toBe(201);
  return r.data;
};
const active = async (environment = ENV) => (await call('GET', `/distributions/active?bank=${BANK}&environment=${environment}`)).data?.distribution;
const states = async (environment = ENV) => Object.fromEntries(((await call('GET', `/distributions?bank=${BANK}&environment=${environment}`)).data as any[]).map((d) => [d.id, d]));
const distId = (t: string) => `dist/${BANK}@${t.split('/').pop()}`;
/** The manifest of a published distribution, from the content branch. */
const manifestOf = (id: string) => parseYaml(server.fake.files(server.repo, 'main')[`content-configurations/releases/distributions/${BANK}/${id.split('@')[1]}/manifest.yaml`]);
const engineManifest = (id: string) => parseYaml(server.fake.files(server.repo, 'main')[`content-configurations/releases/distributions/${BANK}/${id.split('@')[1]}/engine/${PLATFORM}/manifest.yaml`]);

/** A payment at the front door: pinned now to the active distribution; run later (it stays open until it ends). */
let n = 0;
const acceptPayment = async () => {
  const paymentId = `PAY-${tag}-${++n}`;
  return { paymentId, distribution: await accept(rt, { paymentId, bank: BANK, environment: ENV }) };
};
const run = async (p: { paymentId: string; distribution: string }, engine?: string) => {
  const r = await runPinned({
    runtime: rt, bank: BANK, environment: ENV, paymentId: p.paymentId, distribution: p.distribution, workflow: PLATFORM, studioRoot: STUDIO, cache,
    inbound: { message: 'uae.wps.sif', payload: SIF }, deployEnvironment: { evaluationDate: '2026-11-17', bench: BENCH, configurationSet: configSet }, engine: engine as any,
  });
  expect(r.result.outcome, r.result.log.slice(-30).join('\n')).toBe('settled');
  expect(r.ended).toBe(true);
  return r;
};

/** Publishes a change through maker-checker; the approval builds the distributions or the configuration release it calls for. */
async function publish(doc: any, tenant: string, maker: string, checker: string) {
  const d = await api.post('/api/drafts', { tenant, actor: maker, doc });
  expect(d.status, JSON.stringify(d.data).slice(0, 400)).toBe(200);
  const pub = await api.post('/api/publish', { tenant, key: d.data.key ?? d.data.saved?.key ?? d.data.draft?.key, checker });
  expect(pub.status, JSON.stringify(pub.data).slice(0, 800)).toBe(200);
  return pub.data.bundles as { kind: string; releaseId: string; tag: string }[];
}
/** A synthetic bank rule, as a bank configurator writes one. */
const RULE = (name: string) => ({
  kind: 'rule', name, release: '1.0.0', title: 'A synthetic bank rule for the distribution test', ruleKind: 'field', level: 'payment', repeatability: 'non-repeatable',
  when: [{ match: { fact: 'Payment', as: 'p', where: 'p.amount <= 0' } }],
  then: [{ yield: { finding: { severity: 'reject', path: 'payment.amount', reasonCode: 'AM12' } } }],
  examples: [{ name: 'passes', evaluationDate: '2026-11-17', given: { Payment: { amount: 1 } }, expect: { findings: [] } }, { name: 'fails', evaluationDate: '2026-11-17', given: { Payment: { amount: 0 } }, expect: { findings: [{ reasonCode: 'AM12' }] } }],
});
const minor = (release: string) => release.replace(/^(\d+)\.(\d+)\.\d+$/, (_, a, b) => `${a}.${Number(b) + 1}.0`);
const latest = async (name: string) => (await api.get(`/api/artefacts/${name}?tenant=owner`)).data.doc;
const patch = (release: string) => release.replace(/\.(\d+)$/, (_, p) => `.${Number(p) + 1}`);

beforeAll(async () => {
  build(path.join(RUNTIME, 'src', 'TransactHub.Api'));
  build(path.join(RUNTIME, 'tools', 'TransactHub.TemporalHarness'));
  const tts = path.join(RUNTIME, 'tools', 'temporal-test-server');
  if (!fs.existsSync(path.join(tts, 'classpath.txt'))) spawnSync(path.join(tts, 'setup.sh'), { encoding: 'utf8' });
  const name = `th-e2e-dist-pg-${tag}`;
  const pg = spawnSync('docker', ['run', '-d', '--name', name, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine'], { encoding: 'utf8' });
  if (pg.status !== 0) throw new Error(pg.stderr);
  containers.push(name);
  pgPort = spawnSync('docker', ['port', name, '5432'], { encoding: 'utf8' }).stdout.split('\n')[0].split(':').pop()!.trim();
  for (let i = 0; i < 60 && spawnSync('docker', ['exec', name, 'pg_isready', '-U', 'postgres']).status !== 0; i++) await new Promise((r) => setTimeout(r, 1000));
  server = await startServer();
  api = client(server.url);
  await startRuntime();
  // The engines accept each payment into the store, pinned to its distribution (R-09-52).
  process.env.TH_STORE_PROVIDER = 'PostgreSql';
  process.env.TH_STORE_CONNECTION = store();
  configSet = parseYaml(fs.readFileSync(path.join(REPO, 'content-configurations', 'banks', BANK, 'configuration-set.yaml'), 'utf8'));
  // On approval, build the distributions and configuration releases (R-11-40), for the test environment (with simulators).
  const s = await api.put('/api/git', { actor: 'shared.checker', target: 'temporal', distributions: true, testDistributions: true });
  expect(s.status, JSON.stringify(s.data)).toBe(200);
}, 900000);

afterAll(() => {
  server?.stop();
  runtime?.kill();
  delete process.env.TH_STORE_PROVIDER;
  delete process.env.TH_STORE_CONNECTION;
  for (const c of containers) spawnSync('docker', ['rm', '-f', c]);
  fs.rmSync(work, { recursive: true, force: true });
});

const ids: Record<string, string> = {};

describe('packaging and deployment, scenarios 1 to 12 on Temporal with the store on PostgreSQL', () => {
  it('11. a bank onboards: its first distribution and its first configuration release, installed and activated; its first payment settles on it', async () => {
    expect((await call('POST', '/payments/pin', { paymentId: 'PAY-none', bank: BANK, environment: ENV })).status).toBe(409);
    const d = await api.post('/api/distributions', { actor: 'example.checker', bank: BANK, engine: 'temporal', withSimulators: true });
    expect(d.status, JSON.stringify(d.data).slice(0, 600)).toBe(200);
    const versions = (await call('GET', '/sources/versions')).data as any[];
    expect(versions.find((v) => v.tag === d.data.tag)).toMatchObject({ kind: 'distribution', stored: false });
    expect((await install(d.data.tag)).state).toBe('installed');
    ids.first = d.data.releaseId;
    const c = await api.post('/api/configuration-releases', { actor: 'example.checker', bank: BANK, release: '50.0.0' });
    expect(c.status, JSON.stringify(c.data).slice(0, 600)).toBe(200);
    expect((await install(c.data.tag)).checkedAgainst).toEqual([ids.first]);
    // A distribution with simulators is for a test environment only.
    expect((await call('POST', '/distributions/activate', { id: ids.first, environment: 'prod', why: 'e2e' })).status).toBe(409);
    await activate(ids.first);
    const p = await acceptPayment();
    expect(p.distribution).toBe(ids.first);
    const r = await run(p);
    // The engine ran it as this distribution's copy: its task queues and build id carry the distribution.
    expect(r.result.engine.distribution).toMatchObject({ id: ids.first, buildId: ids.first });
    expect(r.result.engine.distribution.taskQueues.every((q: string) => q.endsWith(`@${ids.first}`))).toBe(true);
    const pin = (await call('GET', `/payments/pin?paymentId=${p.paymentId}`)).data;
    expect(pin).toMatchObject({ distribution: ids.first, ended: 'settled' });
  }, 900000);

  it('1. a bank changes a dial value: a configuration release, dated values, no deployment', async () => {
    const before = Object.keys(await states()).length;
    const set = configSet;
    const next = { ...set, release: '50.1.0', values: [...(set.values ?? []).map((v: any) => (v.parameter === 'b.rule.internal-limit.v1#internalLimit' && !v.validTo ? { ...v, validTo: '2027-06-30' } : v)), { parameter: 'b.rule.internal-limit.v1#internalLimit', value: 123456, validFrom: '2027-07-01' }] };
    const saved = await api.post(`/api/tenants/${BANK}/configuration`, { tenant: BANK, actor: 'example.maker', yaml: toYaml(next) });
    expect(saved.status, JSON.stringify(saved.data).slice(0, 400)).toBe(200);
    const pub = await api.post('/api/publish', { tenant: BANK, key: saved.data.draft.key, checker: 'example.checker' });
    expect(pub.status, JSON.stringify(pub.data).slice(0, 600)).toBe(200);
    expect(pub.data.bundles.map((b: any) => b.releaseId)).toEqual([`config/${BANK}@50.1.0`]);
    await install(pub.data.bundles[0].tag);
    const at = async (d: string) => ((await call('GET', `/values?tenant=${BANK}&at=${d}T12:00:00Z`)).data as any[]).find((v) => v.parameter === 'b.rule.internal-limit.v1#internalLimit')?.value;
    expect(await at('2027-07-02')).toBe(123456);
    expect(await at('2027-02-01')).toBe(100000);
    expect(Object.keys(await states()).length).toBe(before);
    expect(await active()).toBe(ids.first);
  }, 300000);

  it('2. a bank switches core banking from the simulator to its gateway: a binding with a later validFrom', async () => {
    const c = await api.post('/api/configuration-releases', {
      actor: 'example.checker', bank: BANK, release: '50.2.0',
      bindings: [
        { port: 'p.port.core-banking.v1', adapter: 'simulator', settings: { simulator: 'p.sim.core-banking.v1' }, validFrom: '2026-11-01' },
        { port: 'p.port.core-banking.v1', adapter: 'gateway-rest', settings: { baseUrl: 'https://core.bank.example.test' }, secretRef: 'env:TH_CORE_BANKING_TOKEN', validFrom: '2027-02-01' },
      ],
    });
    expect(c.status, JSON.stringify(c.data).slice(0, 400)).toBe(200);
    await install(c.data.tag);
    expect((await call('GET', `/bindings?tenant=${BANK}&port=p.port.core-banking.v1&at=2027-01-15T00:00:00Z`)).data.adapter).toBe('simulator');
    expect((await call('GET', `/bindings?tenant=${BANK}&port=p.port.core-banking.v1&at=2027-02-02T00:00:00Z`)).data.adapter).toBe('gateway-rest');
    // A credential as a setting is refused by the studio.
    const bad = await api.post('/api/configuration-releases', { actor: 'example.checker', bank: BANK, release: '50.2.1', bindings: [{ port: 'p.port.core-banking.v1', adapter: 'gateway-rest', settings: { apiToken: 'synthetic' }, validFrom: '2027-02-01' }] });
    expect(bad.status).not.toBe(200);
  }, 300000);

  /** A change that builds a new distribution: payments accepted before finish on the old one, new ones take the new one. */
  async function switchOver(label: string, change: () => Promise<{ kind: string; releaseId: string; tag: string }[]>, expectIn: (newId: string, oldId: string) => void) {
    const old = await active();
    const inFlight = await acceptPayment();
    expect(inFlight.distribution).toBe(old);
    const bundles = await change();
    const dist = bundles.find((b) => b.kind === 'distribution' && b.releaseId.startsWith(`dist/${BANK}@`));
    expect(dist, JSON.stringify(bundles)).toBeDefined();
    await install(dist!.tag);
    const sw = await activate(dist!.releaseId);
    expect(sw.draining).toBe(old);
    const st = await states();
    expect(st[old].state).toBe('draining');
    expect(st[old].openPayments).toBeGreaterThanOrEqual(1);
    // The payment accepted before runs to its end on the old distribution; a new payment takes the new one.
    const r1 = await run(inFlight);
    expect(r1.result.engine.distribution.id, label).toBe(old);
    const fresh = await acceptPayment();
    expect(fresh.distribution).toBe(dist!.releaseId);
    const r2 = await run(fresh);
    expect(r2.result.engine.distribution.id).toBe(dist!.releaseId);
    expectIn(dist!.releaseId, old);
    ids[label] = dist!.releaseId;
    return { old, now: dist!.releaseId };
  }

  it('3. a bank adds a bank rule: a bank pack release, then a new distribution; payments in flight finish on the old one', async () => {
    const rule = RULE('b.rule.e2e-dist-check.v1');
    await switchOver('3', () => publish(rule, BANK, 'example.maker', 'example.checker'), (now, old) => {
      expect(manifestOf(now).billOfMaterials.artefacts.some((a: any) => a.name === 'b.rule.e2e-dist-check.v1')).toBe(true);
      expect(manifestOf(old).billOfMaterials.artefacts.some((a: any) => a.name === 'b.rule.e2e-dist-check.v1')).toBe(false);
    });
  }, 1800000);

  it('4. a fix to a WPS activity: a scheme patch release, then a new distribution per bank that runs WPS', async () => {
    const act = await latest('uae.wps.exchange.submit-salary-file.v1');
    const fixed = { ...act, release: patch(act.release), description: `${act.description} (A fix for the distribution test.)` };
    await switchOver('4', () => publish(fixed, 'owner', 'workflow.developer', 'shared.checker'), (now, old) => {
      expect(JSON.stringify(engineManifest(now).tasks)).toContain(`uae.wps.exchange.submit-salary-file.v1@${fixed.release}`);
      expect(JSON.stringify(engineManifest(old).tasks)).toContain(`uae.wps.exchange.submit-salary-file.v1@${act.release}`);
    });
  }, 1800000);

  it('5. a new version of a platform workflow: a platform release, then a new distribution per bank', async () => {
    const wf = await latest(PLATFORM);
    const next = { ...wf, release: patch(wf.release), description: `${wf.description} (A release for the distribution test.)` };
    await switchOver('5', () => publish(next, 'owner', 'workflow.designer', 'shared.checker'), (now, old) => {
      expect(engineManifest(now).flows.find((f: any) => f.name === PLATFORM).release).toBe(next.release);
      expect(engineManifest(old).flows.find((f: any) => f.name === PLATFORM).release).toBe(wf.release);
    });
  }, 1800000);

  it('12. a rule\'s logic changes (not its value): a scheme release, then a new distribution, as 4', async () => {
    const rule = await latest('uae.wps.rule.agent-is-this-bank.v1');
    const next = { ...rule, release: patch(rule.release), description: `${rule.description ?? rule.title} (Its logic released again for the distribution test.)` };
    await switchOver('12', () => publish(next, 'owner', 'workflow.developer', 'shared.checker'), (now, old) => {
      const rel = (id: string) => manifestOf(id).billOfMaterials.artefacts.find((a: any) => a.name === 'uae.wps.rule.agent-is-this-bank.v1').release;
      expect(rel(now)).toBe(next.release);
      expect(rel(old)).toBe(rule.release);
    });
  }, 1800000);

  it('7. a new dial: a release that adds the parameter, a new distribution, then a configuration release with its value', async () => {
    const act = await latest('uae.wps.exchange.submit-salary-file.v1');
    const dial = 'uae.wps.exchange.submit-salary-file.v1#resubmitWait';
    const next = { ...act, release: minor(act.release), parameters: { ...act.parameters, resubmitWait: { type: 'duration', default: 'PT2H', bounds: { min: 'PT1H', max: 'P1D' }, description: 'A synthetic dial for the distribution test.' } } };
    const valued = (release: string) => ({ ...configSet, release, values: [...configSet.values, { parameter: dial, value: 'PT3H', validFrom: '2027-03-01' }] });
    // Before any distribution declares it, a value for the dial is refused.
    const early = await api.post(`/api/tenants/${BANK}/configuration`, { tenant: BANK, actor: 'example.maker', yaml: toYaml(valued('50.3.0')) });
    if (early.status === 200) expect((await api.post('/api/publish', { tenant: BANK, key: early.data.draft.key, checker: 'example.checker' })).status).not.toBe(200);
    const { now, old } = await switchOver('7', () => publish(next, 'owner', 'workflow.developer', 'shared.checker'), (now, old) => {
      expect(manifestOf(now).parameterContract.parameters.some((p: any) => p.parameter === dial)).toBe(true);
      expect(manifestOf(old).parameterContract.parameters.some((p: any) => p.parameter === dial)).toBe(false);
    });
    const c = await api.post('/api/configuration-releases', { actor: 'example.checker', bank: BANK, release: '50.4.0' });
    expect(c.status).toBe(200);
    // The configuration set carries the new value: the old distribution, which does not declare the dial, ignores it.
    const set = await api.post(`/api/tenants/${BANK}/configuration`, { tenant: BANK, actor: 'example.maker', yaml: toYaml(valued('50.5.0')) });
    expect(set.status, JSON.stringify(set.data).slice(0, 300)).toBe(200);
    const pub = await api.post('/api/publish', { tenant: BANK, key: set.data.draft.key, checker: 'example.checker' });
    expect(pub.status, JSON.stringify(pub.data).slice(0, 600)).toBe(200);
    const inst = await install(pub.data.bundles[0].tag);
    expect(inst.checkedAgainst).toEqual(expect.arrayContaining([now, old]));
  }, 1800000);

  it('8. emergency rollback: the previous distribution again; the newer one\'s open payments finish on it', async () => {
    const newer = await active();
    const open = await acceptPayment();
    expect(open.distribution).toBe(newer);
    const rb = await call('POST', '/distributions/rollback', { bank: BANK, environment: ENV, why: 'incident INC-E2E' });
    expect(rb.status, JSON.stringify(rb.data)).toBe(201);
    expect(rb.data).toMatchObject({ rolledBackFrom: newer, draining: newer, active: ids['12'] });
    const fresh = await acceptPayment();
    expect(fresh.distribution).toBe(ids['12']);
    expect((await run(open)).result.engine.distribution.id).toBe(newer);
    expect((await run(fresh)).result.engine.distribution.id).toBe(ids['12']);
  }, 1800000);

  it('removal: refused while a payment pinned to it is open, and before its longest declared wait has passed', async () => {
    const st = await states();
    const draining = Object.values<any>(st).find((d) => d.state === 'draining' || d.state === 'drained');
    expect(draining).toBeDefined();
    const r = await call('POST', '/distributions/remove', { id: draining.id, environment: ENV, why: 'tidy' });
    expect(r.status).toBe(409);
    expect(r.data.error).toMatch(/open payment|longest declared wait \(P30D\)/);
    expect((await call('POST', '/distributions/remove', { id: await active(), environment: ENV, why: 'tidy' })).data.error).toMatch(/is active/);
  }, 300000);

  it('9. a bank moves to another engine: a distribution with the DTFx binding; payments in flight finish on Temporal', async () => {
    const old = await active();
    const open = await acceptPayment();
    const d = await api.post('/api/distributions', { actor: 'example.checker', bank: BANK, engine: 'dtfx', withSimulators: true });
    expect(d.status, JSON.stringify(d.data).slice(0, 400)).toBe(200);
    expect(d.data.manifest.engine).toBe('dtfx');
    await install(d.data.tag);
    await activate(d.data.releaseId);
    const fresh = await acceptPayment();
    expect(fresh.distribution).toBe(d.data.releaseId);
    const r1 = await run(open);
    expect(r1.result.engine.distribution).toMatchObject({ id: old, buildId: old });
    const r2 = await run(fresh);
    expect(r2.result.engine.distribution).toMatchObject({ id: d.data.releaseId, orchestration: `transacthub.interpreter@${d.data.releaseId}` });
    ids['9'] = d.data.releaseId;
    // Back to Temporal for what follows.
    await call('POST', '/distributions/rollback', { bank: BANK, environment: ENV, why: 'back to temporal' });
  }, 1800000);

  it('10. a runtime software upgrade: the hosts restart on the same store; every installed distribution keeps its state, and open payments finish', async () => {
    const before = await states();
    const open = await acceptPayment();
    runtime?.kill();
    await new Promise((r) => setTimeout(r, 1000));
    await startRuntime();
    const after = await states();
    for (const [id, d] of Object.entries<any>(before)) expect(after[id]?.state, id).toBe(d.state);
    expect((await run(open)).result.engine.distribution.id).toBe(open.distribution);
  }, 1800000);

  it('6. a new scheme reaches only the banks that license it (the slot-contract check refuses one that does not fit)', async () => {
    // The factory has one scheme. A leg that names a slot contract the platform does not offer is refused at the build.
    const leg = await latest('uae.wps.wf.salary-file-out.v1');
    const bad = { ...leg, release: patch(leg.release), implements: { ...leg.implements, of: ['p.wf.corporate.v2'] } };
    const d = await api.post('/api/drafts', { tenant: 'owner', actor: 'workflow.designer', doc: bad });
    if (d.status === 200) {
      const pub = await api.post('/api/publish', { tenant: 'owner', key: d.data.key ?? d.data.draft?.key, checker: 'shared.checker' });
      expect(pub.status).not.toBe(200);
    }
    // Every distribution lists only the schemes its bank licenses.
    for (const id of Object.keys(await states())) expect(manifestOf(id).schemes).toEqual(['uae.wps']);
  }, 600000);
});

// Scenarios 3, 4, 5, 8 and 9 on the other engines: a distribution per engine, payments in flight finish on the old one.
describe.each(['dtfx', 'elsa', 'flowable'] as const)('scenarios 3, 4, 5, 8 and 9 on %s', (engine) => {
  beforeAll(async () => {
    if (engine !== 'flowable' || process.env.TH_FLOWABLE_URL) return;
    const name = `th-e2e-dist-flowable-${tag}`;
    const r = spawnSync('docker', ['run', '-d', '--name', name, '-e', 'FLOWABLE_ASYNC_EXECUTOR_ACTIVATE=false', '-e', 'FLOWABLE_ASYNC_HISTORY_EXECUTOR_ACTIVATE=false', '-e', 'FLOWABLE_REST_APP_CREATE_DEMO_DEFINITIONS=false',
      '-e', 'FLOWABLE_REST_APP_ADMIN_USER_ID=rest-admin', '-e', 'FLOWABLE_REST_APP_ADMIN_PASSWORD=test', '-p', '127.0.0.1::8080', FLOWABLE_IMAGE], { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(r.stderr);
    containers.push(name);
    const port = spawnSync('docker', ['port', name, '8080'], { encoding: 'utf8' }).stdout.split('\n')[0].split(':').pop()!.trim();
    const url = `http://127.0.0.1:${port}/flowable-rest`;
    const auth = `Basic ${Buffer.from('rest-admin:test').toString('base64')}`;
    for (let i = 0; i < 180; i++) { try { if ((await fetch(`${url}/service/management/engine`, { headers: { authorization: auth } })).ok) break; } catch { /* not yet */ } await new Promise((res) => setTimeout(res, 1000)); }
    Object.assign(process.env, { TH_FLOWABLE_URL: url, TH_FLOWABLE_USER: 'rest-admin', TH_FLOWABLE_PASSWORD: 'test' });
  }, 600000);

  const build = async () => {
    const d = await api.post('/api/distributions', { actor: 'example.checker', bank: BANK, engine, withSimulators: true });
    expect(d.status, JSON.stringify(d.data).slice(0, 400)).toBe(200);
    await install(d.data.tag);
    return d.data.releaseId as string;
  };
  const marker = (r: any) => r.result.engine.distribution?.id;

  it(`3, 4 and 5: a bank rule, a WPS fix and a platform release, then a new ${engine} distribution; payments in flight finish on the old one`, async () => {
    const da = await build();
    await activate(da);
    const inFlight = await acceptPayment();
    expect(inFlight.distribution).toBe(da);
    const rule = RULE(`b.rule.e2e-${engine}.v1`);
    await publish(rule, BANK, 'example.maker', 'example.checker');
    const act = await latest('uae.wps.exchange.submit-salary-file.v1');
    await publish({ ...act, release: patch(act.release), description: `${act.description} (${engine})` }, 'owner', 'workflow.developer', 'shared.checker');
    const wf = await latest(PLATFORM);
    await publish({ ...wf, release: patch(wf.release), description: `${wf.description} (${engine})` }, 'owner', 'workflow.designer', 'shared.checker');
    const db = await build();
    expect((await activate(db)).draining).toBe(da);
    const r1 = await run(inFlight, engine);
    expect(marker(r1)).toBe(da);
    const fresh = await acceptPayment();
    expect(fresh.distribution).toBe(db);
    expect(marker(await run(fresh, engine))).toBe(db);
    expect(manifestOf(db).billOfMaterials.artefacts.some((a: any) => a.name === `b.rule.e2e-${engine}.v1`)).toBe(true);
    expect(manifestOf(da).billOfMaterials.artefacts.some((a: any) => a.name === `b.rule.e2e-${engine}.v1`)).toBe(false);
    ids[`${engine}:a`] = da;
    ids[`${engine}:b`] = db;
  }, 3600000);

  it(`8: a rollback on ${engine}`, async () => {
    const open = await acceptPayment();
    expect(open.distribution).toBe(ids[`${engine}:b`]);
    const rb = await call('POST', '/distributions/rollback', { bank: BANK, environment: ENV, why: `incident on ${engine}` });
    expect(rb.data.active).toBe(ids[`${engine}:a`]);
    const fresh = await acceptPayment();
    expect(fresh.distribution).toBe(ids[`${engine}:a`]);
    expect(marker(await run(open, engine))).toBe(ids[`${engine}:b`]);
    expect(marker(await run(fresh, engine))).toBe(ids[`${engine}:a`]);
  }, 1800000);

  it(`9: the bank moves from Temporal to ${engine}`, async () => {
    // A Temporal distribution active, a payment in flight on it; then the bank moves to this engine.
    const t = await api.post('/api/distributions', { actor: 'example.checker', bank: BANK, engine: 'temporal', withSimulators: true });
    await install(t.data.tag);
    await activate(t.data.releaseId);
    const open = await acceptPayment();
    const e = await build();
    expect((await activate(e)).draining).toBe(t.data.releaseId);
    const fresh = await acceptPayment();
    expect(marker(await run(open))).toBe(t.data.releaseId);
    expect(marker(await run(fresh, engine))).toBe(e);
  }, 1800000);
});
