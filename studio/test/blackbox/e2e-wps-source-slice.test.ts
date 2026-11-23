import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { parseYaml } from '../../src/core/yaml.js';
import { zip } from '../../src/export/zip.js';
import { client, startServer, STUDIO, type Server } from './server.js';
import { runtimeApiEnv, runtimeHeaders } from './runtime-auth.js';
import { TEST_TOKEN } from '../support/content.js';
import { devModules } from './native-module.js';

// The north-star vertical slice (plan step 2), end to end, black box:
//   the TransactHub source's WPS outward flow, extracted (docs/import/extract_wps_outward_cbx.py) ->
//   imported into the studio as drafts -> published through maker-checker -> written to the connected
//   branch -> stored by the runtime's release API (PostgreSQL) -> deployed on Temporal, where the salary
//   file is rendered by the source's own Central Bank SIF writer, running in the runtime's native host.
// The SIF it produces is compared field by field with what the source's writer gives for this file, on
// Temporal, DTFx and Elsa (steps 5 to 7).

const REPO = path.resolve(STUDIO, '..');
const RUNTIME = path.join(REPO, 'runtime');
const BUNDLE = path.join(REPO, 'content-configurations', 'extractions', 'wps-outward-cbx');
// The release of the leg the bundle carries: the factory leg's next minor (a published release is never edited, R-01-10).
const LEG: string = parseYaml(fs.readFileSync(path.join(BUNDLE, 'artefacts', 'workflows', 'uae.wps.wf.salary-file-out.v1.yaml'), 'utf8')).release;
const TENANT = 'example-bank';
const PLATFORM = 'p.wf.corporate.v1';
const BENCH = [
  ...['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
];
const PASSWORD = 'Th-Test-Only-1234';
const DOTNET_ENV = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };

let server: Server;
let api: ReturnType<typeof client>;
let native: ChildProcess;
let nativeUrl = '';
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-slice-'));
const containers: string[] = [];

const freePort = () => new Promise<number>((resolve) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as any).port; s.close(() => resolve(p)); }); });
const build = (dir: string) => {
  const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: dir, encoding: 'utf8', env: DOTNET_ENV });
  if (b.status !== 0) throw new Error(`dotnet build ${dir} failed:\n${b.stdout}\n${b.stderr}`);
};
const run = (cmd: string, args: string[], opts: any = {}) => new Promise<string>((resolve) => {
  let text = '';
  const c = spawn(cmd, args, opts);
  const t = setTimeout(() => c.kill('SIGKILL'), 600000);
  c.stdout?.on('data', (d) => (text += d));
  c.stderr?.on('data', (d) => (text += d));
  c.on('close', () => { clearTimeout(t); resolve(text); });
});
const waitFor = async (url: string) => { for (let i = 0; i < 120; i++) { try { if ((await fetch(url)).ok) return; } catch { /* not yet */ } await new Promise((r) => setTimeout(r, 500)); } throw new Error(`${url} did not come up`); };

beforeAll(async () => {
  build(path.join(RUNTIME, 'src', 'TransactHub.Native'));
  build(path.join(RUNTIME, 'src', 'TransactHub.Api'));
  build(path.join(RUNTIME, 'tools', 'TransactHub.TemporalHarness'));
  const tts = path.join(RUNTIME, 'tools', 'temporal-test-server');
  if (!fs.existsSync(path.join(tts, 'classpath.txt'))) spawnSync(path.join(tts, 'setup.sh'), { encoding: 'utf8' });
  nativeUrl = `http://127.0.0.1:${await freePort()}`;
  native = spawn('dotnet', [path.join(RUNTIME, 'src', 'TransactHub.Native', 'bin', 'Release', 'net10.0', 'TransactHub.Native.dll')], { env: { ...DOTNET_ENV, TH_NATIVE_URLS: nativeUrl, TH_NATIVE_DEV_MODULES: devModules() }, stdio: 'ignore' });
  await waitFor(`${nativeUrl}/health`);
  process.env.TH_NATIVE_HOST = nativeUrl;
  server = await startServer({ TH_NATIVE_HOST: nativeUrl });
  api = client(server.url);
}, 900000);
afterAll(() => {
  server?.stop();
  native?.kill();
  for (const c of containers) spawnSync('docker', ['rm', '-f', c]);
});

describe('the source WPS outward flow, through the north-star path', () => {
  let changeSets: string[][] = [];
  let bundleB64 = "";

  it('1. the extraction imports as drafts: every step of the source flow and every class is accounted for', async () => {
    const files: { name: string; content: Buffer }[] = [];
    const walk = (d: string): void => { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, e.name); if (e.isDirectory()) walk(p); else files.push({ name: path.relative(BUNDLE, p).split(path.sep).join('/'), content: fs.readFileSync(p) }); } };
    walk(BUNDLE);
    const bundle = zip(files).toString('base64');
    // A platform user imports the shared part; the bank's configurator imports the bank part (R-11-30).
    const pv = await api.post('/api/import/preview', { bundle });
    expect(pv.data.ok, JSON.stringify(pv.data.issues?.filter((i: any) => i.severity === 'error'))).toBe(true);
    expect(pv.data.inventory.sourceFiles).toMatchObject({ missing: [] });
    expect(pv.data.summary.testsFailed).toBe(0);
    const ap = await api.post('/api/import/apply', { bundle, actor: 'shared.designer' });
    expect(ap.data.applied, JSON.stringify(ap.data).slice(0, 500)).toBe(true);
    changeSets = ap.data.changeSets;
    bundleB64 = bundle;
  }, 300000);

  it('2. it publishes through maker-checker, and each change set lands on the connected branch', async () => {
    expect((await api.post('/api/drafts', {})).status).not.toBe(500);
    const c = await api.put('/api/git', { actor: 'shared.checker', target: 'temporal', environment: 'test' });
    expect(c.status).toBe(200);
    const drafts = [...(await api.get('/api/drafts?tenant=owner')).data, ...(await api.get(`/api/drafts?tenant=${TENANT}`)).data];
    for (const set of changeSets) {
      const d = drafts.find((x: any) => x.key === set[0]);
      if (!d) continue;
      const pub = await api.post('/api/publish', { tenant: d.tenant, key: d.key, checker: d.tenant === 'owner' ? 'shared.checker' : 'example.checker' });
      expect(pub.status, JSON.stringify(pub.data).slice(0, 600)).toBe(200);
      expect(pub.data.git?.status, JSON.stringify(pub.data.git)).toBe('committed');
    }
    const leg = (await api.get('/api/catalogue?q=uae.wps.wf.salary-file-out.v1')).data.items.find((e: any) => e.release === LEG);
    expect(leg?.status).toBe('published');
    // The bank part, once the shared part is a version: imported by the bank configurator, approved by the bank approver.
    const bank = await api.post('/api/import/apply', { bundle: bundleB64, actor: 'example.maker' });
    expect(bank.data.applied, JSON.stringify(bank.data.report?.issues?.filter((i: any) => i.severity === 'error') ?? bank.data).slice(0, 600)).toBe(true);
    for (const set of bank.data.changeSets) {
      const pub = await api.post('/api/publish', { tenant: TENANT, key: set[0], checker: 'example.checker' });
      expect(pub.status, JSON.stringify(pub.data).slice(0, 600)).toBe(200);
    }
  }, 600000);

  it("3. the published leg and the factory's own tests of it are green, with the source's writer in it", async () => {
    for (const id of ['WPS-CBX-006', 'WPS-OUT-001', 'WPS-OUT-002', 'WPS-OUT-003', 'WPS-OUT-004', 'WPS-OUT-005']) {
      const r = await api.post(`/api/tests/${id}/run`, {});
      expect(r.data.passed, `${id}: ${JSON.stringify(r.data.checks?.filter((c: any) => !c.passed))}`).toBe(true);
    }
  }, 300000);

  it("4. the runtime takes the branch and the Temporal package carrying the new leg, on PostgreSQL, activated for test only", async () => {
    // The pipeline's checkout of the content branch: the fake GitHub's tree, written where the runtime reads it.
    const look = path.join(work, 'look');
    for (const [p, text] of Object.entries(server.fake.files(server.repo, 'main'))) {
      if (!p.startsWith('content-configurations/')) continue;
      const f = path.join(look, p.slice('content-configurations/'.length));
      fs.mkdirSync(path.dirname(f), { recursive: true });
      fs.writeFileSync(f, text);
    }
    expect(fs.existsSync(path.join(look, 'artefacts', 'uae.wps', 'uae.wps.map.cb-sif-out.v1', '1.0.0.yaml'))).toBe(true);
    const name = `th-slice-pg-${process.pid}`;
    const pg = spawnSync('docker', ['run', '-d', '--name', name, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine'], { encoding: 'utf8' });
    expect(pg.status, pg.stderr).toBe(0);
    containers.push(name);
    const port = spawnSync('docker', ['port', name], { encoding: 'utf8' }).stdout.split('\n')[0].split(':').pop()!.trim();
    for (let i = 0; i < 60 && spawnSync('docker', ['exec', name, 'pg_isready', '-U', 'postgres']).status !== 0; i++) await new Promise((r) => setTimeout(r, 1000));
    const apiUrl = `http://127.0.0.1:${await freePort()}`;
    const rt = spawn('dotnet', [path.join(RUNTIME, 'src', 'TransactHub.Api', 'bin', 'Release', 'net10.0', 'TransactHub.Api.dll'), '--urls', apiUrl], {
      env: {
        ...DOTNET_ENV, ...runtimeApiEnv(),
        // The studio's content repository as the runtime's package source (R-09-30): read-only, its token by reference.
        PackageSource__GitHub__Repository: server.repo, PackageSource__GitHub__Api: server.fake.url, PackageSource__GitHub__TokenRef: 'env:TH_BB_GH_READ_TOKEN', TH_BB_GH_READ_TOKEN: TEST_TOKEN,
        Store__Provider: 'PostgreSql', Store__Connection: `Host=127.0.0.1;Port=${port};Username=postgres;Password=${PASSWORD};Database=th_slice` }, stdio: 'ignore',
    });
    try {
      for (let i = 0; i < 120; i++) { try { await fetch(`${apiUrl}/activations/active?environment=x&scope=x`); break; } catch { await new Promise((r) => setTimeout(r, 500)); } }
      const imp = await (await fetch(`${apiUrl}/import-branch`, { method: 'POST', headers: runtimeHeaders({ 'content-type': 'application/json' }), body: JSON.stringify({ path: look }) })).json();
      // One bank per runtime (R-09-76): the other banks' configuration sets on the branch are named and not imported, nothing else.
      const problems: string[] = imp.problems ?? [];
      expect(problems.filter((p) => !/^[a-z-]+: the configuration set of [a-z-]+ is not this runtime's bank \(example-bank\), not imported \(R-09-76\)$/.test(p)), JSON.stringify(problems)).toEqual([]);
      expect(problems.some((p) => p.startsWith('example-bank:')), JSON.stringify(problems)).toBe(false);
      // Every engine package the studio published on the branch carried its signature, and the runtime verified each.
      const onBranch = Object.keys(server.fake.files(server.repo, 'main')).filter((p) => /^content-configurations\/releases\/.+\/manifest\.yaml$/.test(p));
      for (const m of onBranch) expect(server.fake.files(server.repo, 'main')[m.replace(/manifest\.yaml$/, 'release.sig')], m).toBeDefined();
      // The branch import stores the workflow packages; the distributions and configuration releases built on approval (on by
      // default, Issue I-113) are installed from the package source below.
      const packages = onBranch.filter((p) => !/^content-configurations\/releases\/(distributions|configuration)\//.test(p));
      expect(imp.stored.length).toBe(packages.length);
      // The same releases, as the runtime's package source sees them on GitHub: one annotated tag per version, each
      // downloaded, verified against the studio's key and found already stored (R-09-30).
      const all = await (await fetch(`${apiUrl}/sources/versions`, { headers: runtimeHeaders() })).json();
      // Of every kind (R-09-40): the workflow packages here; the bank's distribution and configuration release beside them.
      expect(all.some((v: any) => v.kind === 'distribution' && v.workflow === TENANT), JSON.stringify(all)).toBe(true);
      const versions = all.filter((v: any) => v.kind === 'workflow-package');
      expect(versions.length, JSON.stringify(all)).toBe(packages.length);
      for (const v of versions) {
        expect(v.stored, v.tag).toBe(true);
        const inst = await fetch(`${apiUrl}/sources/install`, { method: 'POST', headers: runtimeHeaders({ 'content-type': 'application/json' }), body: JSON.stringify({ tag: v.tag }) });
        expect(inst.status, v.tag).toBe(200);
        expect((await inst.json()).signedBy).toMatch(/^[0-9a-f]{32}$/);
      }
      // A leg is packaged with the platform workflow that uses it: the corporate workflow's Temporal
      // package, which now carries the bundle's leg and the native writer, is stored once and activated for test.
      const z = await api.get(`/api/export/download?workflows=${PLATFORM}&target=temporal&withSimulators=true&tenant=${TENANT}`);
      const stored = await (await fetch(`${apiUrl}/releases`, { method: 'POST', headers: runtimeHeaders(), body: z.data })).json();
      expect(stored, JSON.stringify(stored)).toMatchObject({ scope: `${PLATFORM}/temporal`, stored: true, promotable: false });
      const again = await (await fetch(`${apiUrl}/releases`, { method: 'POST', headers: runtimeHeaders(), body: z.data })).json();
      expect(again.stored).toBe(false);
      const act = await fetch(`${apiUrl}/activations`, { method: 'POST', headers: runtimeHeaders({ 'content-type': 'application/json' }), body: JSON.stringify({ environment: 'test', scope: `${PLATFORM}/temporal`, release: stored.release }) });
      expect(act.status).toBe(201);
      // A package with simulators never reaches production.
      const prod = await fetch(`${apiUrl}/activations`, { method: 'POST', headers: runtimeHeaders({ 'content-type': 'application/json' }), body: JSON.stringify({ environment: 'prod', scope: `${PLATFORM}/temporal`, release: stored.release }) });
      expect(prod.status).toBe(409);
      const got = await (await fetch(`${apiUrl}/releases?scope=${encodeURIComponent(`${PLATFORM}/temporal`)}&release=${stored.release}`, { headers: runtimeHeaders() })).json();
      expect(JSON.stringify(got.manifest.flows)).toContain('uae.wps.wf.salary-file-out.v1');
      expect(JSON.stringify(got.manifest.tasks)).toContain('uae.wps.map.cb-sif-out.v1@1.0.0');
    } finally {
      rt.kill();
    }
  }, 600000);

  /** The SIFs the deployed runs left on the native host, one per engine. */
  const engines: any[] = [];

  /** Deploys the corporate workflow's package for one engine: the salary file settles with the source's writer in it, and its SIF is the writer's. */
  async function settlesWithTheSourceWriter(engine: 'temporal' | 'dtfx' | 'elsa') {
    const z = await api.get(`/api/export/download?workflows=${PLATFORM}&target=${engine}&withSimulators=true&tenant=${TENANT}`);
    expect(z.status).toBe(200);
    const file = path.join(work, `corporate-${engine}.zip`);
    fs.writeFileSync(file, z.data);
    const args = ['src/cli.ts', 'deploy', file, '--workflow', PLATFORM, '--message', 'uae.wps.sif', '--inbound', path.join(STUDIO, 'fixtures', 'samples', 'wps-sif-valid.sif'), '--tenant', TENANT, ...BENCH.flatMap((b) => ['--bench', `${b.port}=${b.simulator}:${b.scenario}`])];
    const before = await (await fetch(`${nativeUrl}/health`)).json();
    const filesBefore: any[] = await (await fetch(`${nativeUrl}/files`)).json();
    const out = await run(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), args, { cwd: STUDIO, env: { ...process.env, TH_NATIVE_HOST: nativeUrl } });
    expect(/^outcome (\w+)/m.exec(out)?.[1], out.slice(-2500)).toBe('settled');
    expect(out).toContain('uae.wps.map.cb-sif-out.v1@1.0.0');
    const after = await (await fetch(`${nativeUrl}/health`)).json();
    expect(after.served['uae.wps.map.cb-sif-out.v1']).toBe((before.served['uae.wps.map.cb-sif-out.v1'] ?? 0) + 1);

    // The host keeps each file once, by its hash. This run's SIF is the one it added; when an earlier
    // engine's run wrote the very same bytes, nothing is added and that earlier file is this run's too.
    const files: any[] = await (await fetch(`${nativeUrl}/files`)).json();
    const sifs = files.filter((f) => f.name === '0000001234567261117083000.SIF');
    const added = sifs.filter((f) => !filesBefore.some((b: any) => b.sha256 === f.sha256));
    expect(added.length).toBeLessThanOrEqual(1);
    const mine = added.length ? added : sifs.filter((f) => engines.some((e) => e.sha256 === f.sha256));
    expect(mine.length, 'the run left no SIF the host kept').toBe(1);
    engines.push(mine[0]);
    for (const sif of mine) {
      const text = await (await fetch(`${nativeUrl}/files/${sif.sha256}`)).text();
      const lines = text.split('\r\n').filter(Boolean);
      // The sample's one employee, as the source's writer pads and formats it, then the control record
      // with the bank's routing code from its configuration; the file creation time is the engine's.
      expect(lines[0]).toBe('EDR,00001234567890,203320101,AE070331234567890123456,2026-10-01,2026-10-31,31,1200.00,300.00,0');
      expect(lines[1]).toMatch(/^SCR,0000001234567,302620122,2026-11-17,\d{4},102026,1,1500.00,AED,REF-0001$/);
      expect(lines).toHaveLength(2);
      expect(text.endsWith('\r\n')).toBe(true);
    }
  }

  it("5. deployed on Temporal, the salary file settles and the Central Bank SIF is the source writer's, field by field", async () => {
    await settlesWithTheSourceWriter('temporal');
  }, 900000);

  // The same published leg on the other two runtime engines (plan P3, the WPS parity suite).
  it("6. deployed on DTFx, the salary file settles and the Central Bank SIF is the source writer's, field by field", async () => {
    await settlesWithTheSourceWriter('dtfx');
  }, 900000);

  it("7. deployed on Elsa, the salary file settles and the Central Bank SIF is the source writer's, field by field", async () => {
    await settlesWithTheSourceWriter('elsa');
  }, 900000);
});
