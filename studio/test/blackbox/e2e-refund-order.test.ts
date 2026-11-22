import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, startServer, STUDIO, type Server } from './server.js';

// End to end, black box (R-12-19 to R-12-22, Issue I-133): a refund request instruction from WPS starts a refund order on the
// platform's p.wf.refund.v1, with WPS as its leg. The order is exported for the example bank and deployed on Temporal (the
// runtime's Temporal harness on the time-skipping test server) and on DTFx, each with the store on PostgreSQL. The platform finds
// the earlier payment, converts and prices the lines on the bank's synthetic rate and charges, blocks, holds for approval and
// posts; the WPS leg returns the funds (DCR) and answers (RRR). Each engine gives the trace the studio's interpreter gives. Then the
// runtime's operations API projects the store into its read model, and the order is there through the operations views: the
// platform view's FX and charges, the scheme view's refund leg, the lines with their own FX and charge. No refund screen.

const TENANT = 'example-bank';
const PLATFORM = 'p.wf.refund.v1';
const MESSAGE = 'uae.wps.rri';
const SAMPLE = 'samples/wps-rri-valid.rri';
const BENCH = [
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-refunds.v1', scenario: 'refund-request' },
  ...['core-banking', 'payment-record', 'channel', 'facts-out', 'encryption'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'approve' },
];
const TOOLS = path.resolve(STUDIO, '..', 'runtime', 'tools');
const OPS_API = path.resolve(STUDIO, '..', 'runtime', 'src', 'TransactHub.Operations.Api');
const TEST_SERVER = path.join(TOOLS, 'temporal-test-server');
const ENGINES = ['temporal', 'dtfx'] as const;
const PASSWORD = 'Th-Test-Only-1234';
const RUN_MS = 900000;

let server: Server;
let api: ReturnType<typeof client>;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-refund-'));
const docker = (...a: string[]) => spawnSync('docker', a, { encoding: 'utf8' });
const started: string[] = [];
const children: ChildProcess[] = [];
let pgPort = '';
const dotnetEnv = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };

beforeAll(async () => {
  if (!fs.existsSync(path.join(TEST_SERVER, 'classpath.txt'))) spawnSync(path.join(TEST_SERVER, 'setup.sh'), { encoding: 'utf8' });
  for (const dir of [path.join(TOOLS, 'TransactHub.TemporalHarness'), path.join(TOOLS, 'TransactHub.DtfxHarness'), OPS_API]) {
    const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: dir, encoding: 'utf8', env: dotnetEnv });
    if (b.status !== 0) throw new Error(`dotnet build failed in ${dir}:\n${b.stdout}\n${b.stderr}`);
  }
  const name = `th-e2e-refund-pg-${process.pid}`;
  const run = docker('run', '-d', '--name', name, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine');
  if (run.status !== 0) throw new Error(run.stderr);
  started.push(name);
  for (let i = 0; i < 90 && docker('exec', name, 'pg_isready', '-U', 'postgres').status !== 0; i++) await new Promise((r) => setTimeout(r, 1000));
  pgPort = docker('port', name, '5432').stdout.split('\n')[0].split(':').pop()!.trim();
  server = await startServer();
  api = client(server.url);
}, RUN_MS);
afterAll(() => {
  server?.stop();
  for (const c of children) c.kill('SIGKILL');
  for (const c of started) docker('rm', '-f', c);
  fs.rmSync(work, { recursive: true, force: true });
});

const connection = (db: string) => `Host=127.0.0.1;Port=${pgPort};Username=postgres;Password=${PASSWORD};Database=${db}`;

async function deploy(engine: string, env: Record<string, string>) {
  const z = await api.get(`/api/export/download?workflows=${PLATFORM}&target=${engine}&withSimulators=true&tenant=${TENANT}`);
  expect(z.status).toBe(200);
  const file = path.join(work, `refund-${engine}.zip`);
  fs.writeFileSync(file, z.data);
  const args = ['src/cli.ts', 'deploy', file, '--workflow', PLATFORM, '--message', MESSAGE, '--inbound', path.join(STUDIO, 'fixtures', SAMPLE), '--tenant', TENANT,
    ...BENCH.flatMap((b) => ['--bench', `${b.port}=${b.simulator}:${b.scenario}`])];
  const out = await new Promise<string>((resolve) => {
    let text = '';
    const child = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), args, { cwd: STUDIO, env: { ...process.env, ...env } });
    const timer = setTimeout(() => child.kill('SIGKILL'), RUN_MS);
    child.stdout.on('data', (d) => (text += d));
    child.stderr.on('data', (d) => (text += d));
    child.on('close', () => { clearTimeout(timer); resolve(text); });
  });
  return { out, trace: /^trace  (.*)$/m.exec(out)?.[1].split(' | ') ?? [], outcome: /^outcome (\w+)/m.exec(out)?.[1] };
}

async function freePort(): Promise<number> {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.listen(0, '127.0.0.1', () => { const p = (s.address() as net.AddressInfo).port; s.close(() => resolve(p)); });
  });
}

/** The runtime's operations API over the store's database: its projection builds the read model from the outbox. */
async function opsApi(db: string) {
  const port = await freePort();
  const child = spawn('dotnet', ['run', '-c', 'Release', '--no-build', '--project', OPS_API, '--urls', `http://127.0.0.1:${port}`], {
    cwd: path.resolve(STUDIO, '..'),
    env: { ...dotnetEnv, ASPNETCORE_ENVIRONMENT: 'Development', TH_AUTH_DEV: '1', Store__Provider: 'PostgreSql', Store__Connection: connection(db),
      Operations__Views: path.resolve(STUDIO, '..', 'content-configurations', 'artefacts'), Operations__PollMilliseconds: '200' },
  });
  children.push(child);
  let log = '';
  child.stdout?.on('data', (d) => (log += d));
  child.stderr?.on('data', (d) => (log += d));
  const url = `http://127.0.0.1:${port}/ops/api`;
  const who = JSON.stringify({ sub: 'operator-synthetic-1', roles: [`${TENANT}:Operator`] });
  const get = async (p: string) => (await fetch(`${url}${p}`, { headers: { 'X-TH-Dev-Identity': who } })).json() as Promise<any>;
  for (let i = 0; ; i++) {
    try { if ((await fetch(`${url}/health`)).ok) break; } catch { /* not up yet */ }
    if (child.exitCode !== null || i > 240) throw new Error(`The operations API did not start: ${log.slice(-2000)}`);
    await new Promise((r) => setTimeout(r, 500));
  }
  return { get, stop: () => child.kill('SIGKILL'), log: () => log };
}

describe('a refund order with WPS as its leg, on Temporal and DTFx, seen in the operations console', () => {
  let expected: any;
  it('runs on the studio interpreter through FX, charges, the block, the approval and the posting', async () => {
    const r = await api.post('/api/bench/run', { target: PLATFORM, tenant: TENANT, inbound: SAMPLE, message: MESSAGE, bench: BENCH });
    expect(r.status, JSON.stringify(r.data).slice(0, 400)).toBe(200);
    expected = r.data;
    expect(expected.outcome).toBe('settled');
    expect(expected.trace).toEqual(['original:found', 'fx:converted', 'charges:priced', 'reserve:reserved', 'approval:approved', 'post:posted', 'verify:verified', 'leg:settled', 'finally:notify', 'finally:publish', 'end:settled']);
  });

  for (const engine of ENGINES) {
    it(`${engine}: the same trace, WPS in the slot, kept in the store and read back from the operations read model`, async () => {
      const db = `th_refund_${engine}`;
      const d = await deploy(engine, { TH_STORE_PROVIDER: 'PostgreSql', TH_STORE_CONNECTION: connection(db) });
      expect(d.outcome, d.out.slice(-3000)).toBe('settled');
      expect(d.trace).toEqual(expected.trace);
      expect(d.out).toContain('front door: uae.wps.wf.refund-request-in.v1 fills scheme-leg');
      expect(d.out).toMatch(/^store: PostgreSql holds 1 bulk\(s\), 2 payment\(s\)/m);

      const ops = await opsApi(db);
      try {
        let row: any;
        for (let i = 0; i < 120 && !row?.legs?.length; i++) {
          const list = await ops.get(`/payments?tenant=${TENANT}&scheme=uae.wps`);
          row = (list.rows ?? []).find((x: any) => x.kind === 'bulk');
          if (!row?.legs?.length) await new Promise((r) => setTimeout(r, 500));
        }
        expect(row, ops.log().slice(-2000)).toBeDefined();
        const detail = await ops.get(`/payments/${encodeURIComponent(row.id)}`);
        const f = detail.payment.fields;
        // The platform view: the order, its earlier payment, its FX and its charges (p.view.operations.v1 1.2.0).
        expect(f).toMatchObject({ originalPaymentId: '000000000000000000000001', originalFound: 'found', fx: 'converted', fxCurrency: 'AED',
          refundCharges: 'priced', reservation: 'reserved', posting: 'posted' });
        expect(Number(f.fxAmount)).toBe(600);
        expect(Number(f.refundCharge)).toBe(10.6);
        expect(Number(f.refundChargeTotal)).toBe(11.14);
        // The scheme view: WPS is a leg of the order (uae.wps.view.operations.v1 1.3.0), told apart by kind refund.
        const leg = detail.payment.legs.find((l: any) => l.leg === 'uae.wps.wf.refund-request-in.v1');
        expect(leg, JSON.stringify(detail.payment.legs)).toBeDefined();
        expect(leg.fields).toMatchObject({ dcrStatus: 'ack', rrrStatus: 'ack' });
        // Each line carries its own FX and charge.
        expect(detail.lines.map((l: any) => Number(l.fields.refundCharge))).toEqual([5.1, 5.5]);
        expect(detail.lines.map((l: any) => l.fields.refundReason)).toEqual(['005', '003']);
      } finally {
        ops.stop();
      }
    }, RUN_MS);
  }
});
