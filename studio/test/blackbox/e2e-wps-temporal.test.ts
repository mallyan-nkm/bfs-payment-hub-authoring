import { spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, startServer, STUDIO, type Server } from './server.js';

// End to end, black box, on Temporal (plan P1b): the factory WPS salary file, exported for the bank
// with the Temporal binding and deployed through the runtime's Temporal harness: the front door, the
// corporate payment workflow, the WPS leg in its slot, each activity on the Temporal task queue of
// its scope, served by a separate worker process per queue. Temporal is the time-skipping test
// server (runtime/tools/temporal-test-server), so the day's wait for the acknowledgement takes seconds.
//
// With TH_TEMPORAL_ADDRESS=host:port the runs go to that Temporal server instead, in real time (for
// example the server on SQL Server, runtime/temporal-mssql): the test server is not set up, and a run
// may take as long as the business time it waits (the scheme's acknowledgement comes after 10 minutes).

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
const OPS = (scenario: string) => ({ port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario });
const HARNESS = path.resolve(STUDIO, '..', 'runtime', 'tools', 'TransactHub.TemporalHarness');
const TEST_SERVER = path.resolve(STUDIO, '..', 'runtime', 'tools', 'temporal-test-server');
const EXTERNAL_TEMPORAL = process.env.TH_TEMPORAL_ADDRESS;
// One deployment's run: minutes on the time-skipping test server, the business time it waits on a real one.
const RUN_MS = EXTERNAL_TEMPORAL ? 45 * 60 * 1000 : 600000;

let server: Server;
let api: ReturnType<typeof client>;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-wps-temporal-'));

beforeAll(async () => {
  if (EXTERNAL_TEMPORAL) console.log(`Temporal: the server at ${EXTERNAL_TEMPORAL}, in real time`);
  else if (!fs.existsSync(path.join(TEST_SERVER, 'classpath.txt'))) spawnSync(path.join(TEST_SERVER, 'setup.sh'), { encoding: 'utf8' });
  const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: HARNESS, encoding: 'utf8', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
  if (b.status !== 0) throw new Error(`dotnet build failed:\n${b.stdout}\n${b.stderr}`);
  server = await startServer();
  api = client(server.url);
}, RUN_MS);
afterAll(() => server?.stop());

async function deploy(label: string, sample = SAMPLE, extra: any[] = [], extraEnv: Record<string, string> = {}, flow = { platform: PLATFORM, message: MESSAGE, bench: BENCH }) {
  const z = await api.get(`/api/export/download?workflows=${flow.platform}&target=temporal&withSimulators=true&tenant=${TENANT}`);
  expect(z.status).toBe(200);
  const file = path.join(work, `${label}.zip`);
  fs.writeFileSync(file, z.data);
  const bench = [...flow.bench.filter((b) => !extra.some((o) => o.port === b.port)), ...extra];
  const args = ['src/cli.ts', 'deploy', file, '--workflow', flow.platform, '--message', flow.message, '--inbound', path.join(STUDIO, 'fixtures', sample), '--tenant', TENANT, ...bench.flatMap((b) => ['--bench', `${b.port}=${b.simulator}:${b.scenario}`])];
  // Asynchronous, so the studio server's kept-alive connections stay open while Temporal runs.
  const out = await new Promise<string>((resolve) => {
    let text = '';
    const child = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), args, { cwd: STUDIO, env: { ...process.env, ...extraEnv } });
    const timer = setTimeout(() => child.kill('SIGKILL'), RUN_MS);
    child.stdout.on('data', (d) => (text += d));
    child.stderr.on('data', (d) => (text += d));
    child.on('close', () => { clearTimeout(timer); resolve(text); });
  });
  const workers = [...out.matchAll(/worker (th-[\w-]+) \(pid (\d+)\) served (\{.*\}), refused (\d+)/g)].map((m) => ({ queue: m[1], pid: m[2], served: JSON.parse(m[3]), refused: Number(m[4]) }));
  return { out, workers, trace: /^trace  (.*)$/m.exec(out)?.[1].split(' | ') ?? [], outcome: /^outcome (\w+)/m.exec(out)?.[1] };
}

describe('the WPS salary file on Temporal', () => {
  it('settles through the front door, the corporate workflow and the WPS leg, on four task queues and four workers', async () => {
    const d = await deploy('settle');
    expect(d.outcome, d.out.slice(-2500)).toBe('settled');
    expect(d.trace).toEqual(SETTLED);
    expect(d.out).toContain('front door: uae.wps.wf.salary-file-out.v1 fills scheme-leg');
    expect(d.workers.map((w) => w.queue).sort()).toEqual(['th-bank', 'th-p', 'th-uae-p', 'th-uae-wps']);
    expect(new Set(d.workers.map((w) => w.pid)).size).toBe(4);
    const scope: Record<string, string> = { 'th-bank': 'b.', 'th-p': 'p.', 'th-uae-p': 'uae.p.', 'th-uae-wps': 'uae.wps.' };
    for (const w of d.workers) {
      expect(w.refused).toBe(0);
      expect(Object.keys(w.served).every((t) => t.startsWith(scope[w.queue])), `${w.queue} ${Object.keys(w.served)}`).toBe(true);
    }
    // The salary file went to the scheme and its acknowledgement came back.
    expect(d.workers.find((w) => w.queue === 'th-uae-wps')!.served['uae.wps.exchange.send-salary-file.v1@1.0.0']).toBe(1);
  }, RUN_MS);

  // The same salary file and the same party behaviour give the same trace on Temporal as on the
  // studio's own interpreter (R-09-07), for each way the payment can end.
  const cases: [string, any[]][] = [
    ['the scheme rejects the file', [{ port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'reject' }]],
    ['sanctions screening hits', [{ port: 'p.port.sanctions.v1', simulator: 'p.sim.sanctions.v1', scenario: 'reject' }]],
    ['a file of two lines, with operations declining any hold', [OPS('decline')]],
  ];
  for (const [what, override] of cases) {
    it(`${what}: the same trace as the studio interpreter`, async () => {
      const bench = [...BENCH.filter((b) => !override.some((o) => o.port === b.port)), ...override];
      const sample = what.includes('two lines') ? TWO_LINES : SAMPLE;
      const expected = await api.post('/api/bench/run', { target: PLATFORM, tenant: TENANT, inbound: sample, message: MESSAGE, bench });
      expect(expected.status, JSON.stringify(expected.data).slice(0, 400)).toBe(200);
      const d = await deploy(what.replace(/\W+/g, '-'), sample, override);
      expect(d.trace, d.out.slice(-2500)).toEqual(expected.data.trace);
      expect(d.outcome).toBe(expected.data.outcome);
    }, RUN_MS);
  }

  // Plan P2: the WPS payment file with the bank as the agent, in the source's layout, as e2e-wps-parity runs it on DTFx and Elsa.
  const INWARD = {
    platform: 'p.wf.inward.v1', message: 'uae.wps.pif', sample: 'samples/wps-pif-ssm.pif',
    bench: [
      { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-inward.v1', scenario: 'payment-file-returns-refused' },
      { port: 'p.port.core-banking.v1', simulator: 'p.sim.core-banking.v1', scenario: 'reject-first-then-accept' },
      ...['sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
      { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'reject' },
    ],
  };
  const inwardCases: [string, any[]][] = [
    ['every credit posted: confirmed, credited and reported', [{ port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-inward.v1', scenario: 'payment-file' }, { port: 'p.port.core-banking.v1', simulator: 'p.sim.core-banking.v1', scenario: 'accept' }]],
    ['a refused credit returned, the returns file refused and handed to operations', []],
  ];
  // The PSI the inward simulator sends with the payment file reaches the leg: the platform workflow hands a kept message to its
  // leg once the leg has started (Issue I-84, R-09-16).
  for (const [what, override] of inwardCases) {
    it(`the inward payment file, ${what}: the same trace as the studio interpreter`, async () => {
      const bench = [...INWARD.bench.filter((b) => !override.some((o) => o.port === b.port)), ...override];
      const expected = await api.post('/api/bench/run', { target: INWARD.platform, tenant: TENANT, inbound: INWARD.sample, message: INWARD.message, bench });
      expect(expected.status, JSON.stringify(expected.data).slice(0, 400)).toBe(200);
      const d = await deploy(`inward-${what.replace(/\W+/g, '-')}`, INWARD.sample, override, {}, INWARD);
      expect(d.trace, d.out.slice(-2500)).toEqual(expected.data.trace);
      // The leg ran past the supplementary file to the disbursement file: the PSI reached it (R-05-30).
      expect(d.out).toContain('"uae.wps.map.dif-out.v2@2.0.0":1');
      expect(d.outcome).toBe(expected.data.outcome);
    }, RUN_MS);
  }

  // The payments of a deployment live in the database (plan W2): the salary file is accepted into the
  // ISO 20022 tables, and every change to its payload is a fact; rebuilt from the facts, each payload
  // is what the run ended with. The same on PostgreSQL and on SQL Server.
  const PASSWORD = 'Th-Test-Only-1234';
  const docker = (...a: string[]) => spawnSync('docker', a, { encoding: 'utf8' });
  const started: string[] = [];
  afterAll(() => { for (const c of started) docker('rm', '-f', c); });
  async function database(kind: 'PostgreSql' | 'SqlServer') {
    const name = `th-e2e-${kind.toLowerCase()}-${process.pid}`;
    const run = kind === 'PostgreSql'
      ? docker('run', '-d', '--name', name, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine')
      : docker('run', '-d', '--name', name, '-e', 'ACCEPT_EULA=Y', '-e', `MSSQL_SA_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::1433', 'mcr.microsoft.com/mssql/server:2022-latest');
    if (run.status !== 0) throw new Error(run.stderr);
    started.push(name);
    const port = docker('port', name).stdout.split('\n')[0].split(':').pop()!.trim();
    const connection = kind === 'PostgreSql'
      ? `Host=127.0.0.1;Port=${port};Username=postgres;Password=${PASSWORD};Database=th_e2e`
      : `Server=127.0.0.1,${port};User Id=sa;Password=${PASSWORD};TrustServerCertificate=true;Database=th_e2e`;
    // Ready when the server answers a query.
    for (let i = 0; i < 90; i++) {
      const probe = kind === 'PostgreSql'
        ? docker('exec', name, 'pg_isready', '-U', 'postgres')
        : docker('exec', name, '/opt/mssql-tools18/bin/sqlcmd', '-C', '-S', 'localhost', '-U', 'sa', '-P', PASSWORD, '-Q', 'SELECT 1');
      if (probe.status === 0) break;
      await new Promise((r) => setTimeout(r, 1000));
    }
    return { TH_STORE_PROVIDER: kind, TH_STORE_CONNECTION: connection };
  }

  for (const kind of ['PostgreSql', 'SqlServer'] as const) {
    it(`keeps the payment in the database on ${kind}: accepted as ISO 20022, every payload change a fact`, async () => {
      const env = await database(kind);
      const d = await deploy(`store-${kind}`, SAMPLE, [], env);
      expect(d.outcome, d.out.slice(-2500)).toBe('settled');
      expect(d.trace).toEqual(SETTLED);
      const line = /^store: .*$/m.exec(d.out)?.[0] ?? '';
      expect(line, d.out.slice(-2500)).toMatch(new RegExp(`^store: ${kind} holds 1 bulk\\(s\\), 1 payment\\(s\\), \\d+ part\\(ies\\); \\d+ payload\\(s\\) rebuilt from their facts, chains unbroken, all match the run$`));
    }, RUN_MS);
  }
});
