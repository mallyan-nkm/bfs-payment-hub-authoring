import { spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, startServer, STUDIO, type Server } from './server.js';

// End to end, black box, on DTFx, Elsa and Flowable with the store (plan N4): the factory WPS salary file,
// exported for the bank with each engine's binding and deployed with TH_STORE_PROVIDER set, so the deploy
// runner runs the runtime's build of the engine harness (runtime/tools/TransactHub.{Dtfx,Elsa}Harness), whose
// claim check is journalled to the database. Flowable runs as its own server (plan D3): the Flowable REST
// server, here a container (or TH_FLOWABLE_URL), gets the package's BPMN, and the runtime's binding
// (runtime/tools/TransactHub.FlowableHarness) works its jobs over REST with the payloads in the store. The
// payment must settle as it does without a store, be accepted into the ISO 20022 tables, and every payload
// must rebuild from its facts. Each engine on PostgreSQL and on SQL Server; with Temporal (e2e-wps-temporal)
// that is the four-engine matrix.

const TENANT = 'example-bank';
const SAMPLE = 'samples/wps-sif-valid.sif';
const TWO_LINES = 'samples/wps-sif-two-lines.sif';
const MESSAGE = 'uae.wps.sif';
const PLATFORM = 'p.wf.corporate.v1';
const BENCH = [
  ...['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
];
const SETTLED = ['validate:pass', 'duplicate:unique', 'hook:done', 'sanctions:clear', 'fraud:clear', 'join:all', 'limits:pass', 'reserve:reserved', 'route:decided', 'hook:continue', 'leg:settled', 'post:posted', 'verify:verified', 'finally:notify', 'finally:publish', 'hook:done', 'end:settled'];
const TOOLS = path.resolve(STUDIO, '..', 'runtime', 'tools');
const ENGINES = ['dtfx', 'elsa', 'flowable'] as const;
// The Flowable REST server, pinned to the Flowable of the kit harness (studio/engines/flowable/harness/pom.xml).
const FLOWABLE_IMAGE = 'flowable/flowable-rest:7.1.0@sha256:b7ae287502cd5c243b577a0ca2894172e1c18cd75aea4fd67dc7d58f1b40db44';
const FLOWABLE_USER = 'th-binding';
const FLOWABLE_PASSWORD = 'th-test-only';
const PASSWORD = 'Th-Test-Only-1234';

let server: Server;
let api: ReturnType<typeof client>;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-wps-store-'));
const docker = (...a: string[]) => spawnSync('docker', a, { encoding: 'utf8' });
const started: string[] = [];
const servers: Record<string, { name: string; port: string }> = {};

beforeAll(async () => {
  for (const h of ['TransactHub.DtfxHarness', 'TransactHub.ElsaHarness', 'TransactHub.FlowableHarness']) {
    const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: path.join(TOOLS, h), encoding: 'utf8', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
    if (b.status !== 0) throw new Error(`dotnet build failed:\n${b.stdout}\n${b.stderr}`);
  }
  server = await startServer();
  api = client(server.url);
}, 900000);
afterAll(() => {
  server?.stop();
  for (const c of started) docker('rm', '-f', c);
});

/** One database server per kind, started once; each engine gets a database of its own on it. */
async function database(kind: 'PostgreSql' | 'SqlServer', db: string) {
  if (!servers[kind]) {
    const name = `th-e2e-store-${kind.toLowerCase()}-${process.pid}`;
    const run = kind === 'PostgreSql'
      ? docker('run', '-d', '--name', name, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine')
      : docker('run', '-d', '--name', name, '-e', 'ACCEPT_EULA=Y', '-e', `MSSQL_SA_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::1433', 'mcr.microsoft.com/mssql/server:2022-latest');
    if (run.status !== 0) throw new Error(run.stderr);
    started.push(name);
    for (let i = 0; i < 90; i++) {
      const probe = kind === 'PostgreSql'
        ? docker('exec', name, 'pg_isready', '-U', 'postgres')
        : docker('exec', name, '/opt/mssql-tools18/bin/sqlcmd', '-C', '-S', 'localhost', '-U', 'sa', '-P', PASSWORD, '-Q', 'SELECT 1');
      if (probe.status === 0) break;
      await new Promise((r) => setTimeout(r, 1000));
    }
    servers[kind] = { name, port: docker('port', name).stdout.split('\n')[0].split(':').pop()!.trim() };
  }
  const { port } = servers[kind];
  const connection = kind === 'PostgreSql'
    ? `Host=127.0.0.1;Port=${port};Username=postgres;Password=${PASSWORD};Database=${db}`
    : `Server=127.0.0.1,${port};User Id=sa;Password=${PASSWORD};TrustServerCertificate=true;Database=${db}`;
  return { TH_STORE_PROVIDER: kind, TH_STORE_CONNECTION: connection };
}

/**
 * The Flowable REST server, started once: TH_FLOWABLE_URL when set, else a container. Its async executor is
 * off, because the binding runs the jobs on its own clock.
 */
let flowable: Record<string, string> | undefined;
async function flowableServer() {
  if (flowable) return flowable;
  if (process.env.TH_FLOWABLE_URL) return (flowable = { TH_FLOWABLE_URL: process.env.TH_FLOWABLE_URL });
  const name = `th-e2e-store-flowable-${process.pid}`;
  const run = docker('run', '-d', '--name', name, '-e', 'FLOWABLE_ASYNC_EXECUTOR_ACTIVATE=false', '-e', 'FLOWABLE_ASYNC_HISTORY_EXECUTOR_ACTIVATE=false',
    '-e', 'FLOWABLE_REST_APP_CREATE_DEMO_DEFINITIONS=false', '-e', `FLOWABLE_REST_APP_ADMIN_USER_ID=${FLOWABLE_USER}`, '-e', `FLOWABLE_REST_APP_ADMIN_PASSWORD=${FLOWABLE_PASSWORD}`,
    '-p', '127.0.0.1::8080', FLOWABLE_IMAGE);
  if (run.status !== 0) throw new Error(run.stderr);
  started.push(name);
  const port = docker('port', name, '8080').stdout.split('\n')[0].split(':').pop()!.trim();
  const url = `http://127.0.0.1:${port}/flowable-rest`;
  const auth = `Basic ${Buffer.from(`${FLOWABLE_USER}:${FLOWABLE_PASSWORD}`).toString('base64')}`;
  for (let i = 0; ; i++) {
    try {
      if ((await fetch(`${url}/service/management/engine`, { headers: { authorization: auth } })).ok) break;
    } catch {
      // not up yet
    }
    if (i > 180) throw new Error(`The Flowable REST server did not start: ${docker('logs', '--tail', '30', name).stdout}`);
    await new Promise((r) => setTimeout(r, 1000));
  }
  return (flowable = { TH_FLOWABLE_URL: url, TH_FLOWABLE_USER: FLOWABLE_USER, TH_FLOWABLE_PASSWORD: FLOWABLE_PASSWORD });
}

async function deploy(engine: string, label: string, env: Record<string, string>, flow = { platform: PLATFORM, message: MESSAGE, sample: SAMPLE, bench: BENCH }) {
  const z = await api.get(`/api/export/download?workflows=${flow.platform}&target=${engine}&withSimulators=true&tenant=${TENANT}`);
  expect(z.status).toBe(200);
  const file = path.join(work, `${label}.zip`);
  fs.writeFileSync(file, z.data);
  const args = ['src/cli.ts', 'deploy', file, '--workflow', flow.platform, '--message', flow.message, '--inbound', path.join(STUDIO, 'fixtures', flow.sample), '--tenant', TENANT, ...flow.bench.flatMap((b) => ['--bench', `${b.port}=${b.simulator}:${b.scenario}`])];
  const out = await new Promise<string>((resolve) => {
    let text = '';
    const child = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), args, { cwd: STUDIO, env: { ...process.env, ...env } });
    const timer = setTimeout(() => child.kill('SIGKILL'), 600000);
    child.stdout.on('data', (d) => (text += d));
    child.stderr.on('data', (d) => (text += d));
    child.on('close', () => { clearTimeout(timer); resolve(text); });
  });
  return { out, trace: /^trace  (.*)$/m.exec(out)?.[1].split(' | ') ?? [], outcome: /^outcome (\w+)/m.exec(out)?.[1] };
}

describe('the WPS salary file on DTFx, Elsa and Flowable, kept in the store', () => {
  for (const engine of ENGINES) {
    for (const kind of ['PostgreSql', 'SqlServer'] as const) {
      it(`${engine} on ${kind}: settles, accepted as ISO 20022, every payload rebuilt from its facts`, async () => {
        const env = { ...(await database(kind, `th_${engine}`)), ...(engine === 'flowable' ? await flowableServer() : {}) };
        const d = await deploy(engine, `${engine}-${kind}`, env);
        expect(d.outcome, d.out.slice(-2500)).toBe('settled');
        expect(d.trace).toEqual(SETTLED);
        const line = /^store: .*$/m.exec(d.out)?.[0] ?? '';
        // On Flowable the run was the REST server's: the package's processes deployed and worked over REST.
        if (engine === 'flowable') expect(d.out).toMatch(/^\[engine\] Flowable 7\.1\.0 at the REST server, tenant th-\w+$/m);
        expect(line, d.out.slice(-2500)).toMatch(new RegExp(`^store: ${kind} holds 1 bulk\\(s\\), 1 payment\\(s\\), \\d+ part\\(ies\\); [1-9]\\d* payload\\(s\\) rebuilt from their facts, chains unbroken, all match the run$`));
      }, 900000);
    }
  }
});

// Plan P2: the WPS payment file with the bank as the agent, in the source's layout, on Flowable as on DTFx, Elsa (e2e-wps-parity)
// and Temporal (e2e-wps-temporal): a refused credit returned, WPS refusing the returns file, the file handed to operations.
const INWARD = {
  platform: 'p.wf.inward.v1', message: 'uae.wps.pif', sample: 'samples/wps-pif-ssm.pif',
  bench: [
    { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-inward.v1', scenario: 'payment-file-returns-refused' },
    { port: 'p.port.core-banking.v1', simulator: 'p.sim.core-banking.v1', scenario: 'reject-first-then-accept' },
    ...['sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
    { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'reject' },
  ],
};
describe('the WPS payment file on Flowable, kept in the store (plan P2)', () => {
  it('flowable on PostgreSql: the same trace as the studio interpreter', async () => {
    const expected = await api.post('/api/bench/run', { target: INWARD.platform, tenant: TENANT, inbound: INWARD.sample, message: INWARD.message, bench: INWARD.bench });
    expect(expected.status, JSON.stringify(expected.data).slice(0, 400)).toBe(200);
    const env = { ...(await database('PostgreSql', 'th_flowable_inward')), ...(await flowableServer()) };
    const d = await deploy('flowable', 'flowable-inward', env, INWARD);
    expect(d.trace, d.out.slice(-2500)).toEqual(expected.data.trace);
    expect(d.outcome).toBe(expected.data.outcome);
    expect(d.out).toContain('front door: uae.wps.wf.agent-payment-in.v2 fills scheme-leg');
    // The leg ran past the supplementary file to the disbursement file: the PSI reached it (R-05-30).
    expect(d.out).toContain('"uae.wps.map.dif-out.v2@2.0.0":1');
  }, 900000);
});
