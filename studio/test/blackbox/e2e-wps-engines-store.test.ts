import { spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, startServer, STUDIO, type Server } from './server.js';

// End to end, black box, on DTFx and Elsa with the store (plan N4): the factory WPS salary file, exported
// for the bank with each engine's binding and deployed with TH_STORE_PROVIDER set, so the deploy runner
// runs the runtime's build of the engine harness (runtime/tools/TransactHub.{Dtfx,Elsa}Harness), whose
// claim check is journalled to the database. The payment must settle as it does without a store, be
// accepted into the ISO 20022 tables, and every payload must rebuild from its facts. Each engine on
// PostgreSQL and on SQL Server; with Temporal (e2e-wps-temporal) that is the four-engine matrix less Flowable.

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
const ENGINES = ['dtfx', 'elsa'] as const;
const PASSWORD = 'Th-Test-Only-1234';

let server: Server;
let api: ReturnType<typeof client>;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-wps-store-'));
const docker = (...a: string[]) => spawnSync('docker', a, { encoding: 'utf8' });
const started: string[] = [];
const servers: Record<string, { name: string; port: string }> = {};

beforeAll(async () => {
  for (const h of ['TransactHub.DtfxHarness', 'TransactHub.ElsaHarness']) {
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

async function deploy(engine: string, label: string, env: Record<string, string>) {
  const z = await api.get(`/api/export/download?workflows=${PLATFORM}&target=${engine}&withSimulators=true&tenant=${TENANT}`);
  expect(z.status).toBe(200);
  const file = path.join(work, `${label}.zip`);
  fs.writeFileSync(file, z.data);
  const args = ['src/cli.ts', 'deploy', file, '--workflow', PLATFORM, '--message', MESSAGE, '--inbound', path.join(STUDIO, 'fixtures', SAMPLE), '--tenant', TENANT, ...BENCH.flatMap((b) => ['--bench', `${b.port}=${b.simulator}:${b.scenario}`])];
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

describe('the WPS salary file on DTFx and Elsa, kept in the store', () => {
  for (const engine of ENGINES) {
    for (const kind of ['PostgreSql', 'SqlServer'] as const) {
      it(`${engine} on ${kind}: settles, accepted as ISO 20022, every payload rebuilt from its facts`, async () => {
        const env = await database(kind, `th_${engine}`);
        const d = await deploy(engine, `${engine}-${kind}`, env);
        expect(d.outcome, d.out.slice(-2500)).toBe('settled');
        expect(d.trace).toEqual(SETTLED);
        const line = /^store: .*$/m.exec(d.out)?.[0] ?? '';
        expect(line, d.out.slice(-2500)).toMatch(new RegExp(`^store: ${kind} holds 1 bulk\\(s\\), 1 payment\\(s\\), \\d+ part\\(ies\\); [1-9]\\d* payload\\(s\\) rebuilt from their facts, chains unbroken, all match the run$`));
      }, 900000);
    }
  }
});
