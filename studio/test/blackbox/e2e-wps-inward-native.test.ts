import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, startServer, STUDIO, type Server } from './server.js';

// End to end, black box (plan P2, R-08-16): the WPS payment file, the bank as the agent, with every WPS file on the runtime.
// The runtime's native host answers the payment file (the source's PIF parser), the receipt confirmation and the disbursement
// and returns files (the source's builders), every acknowledgement, the posting window, the credits and the return's posting
// (core banking through the example bank's binding, the simulator adapter). The same run on the studio's own scripts gives the
// same trace. A copy of the banks' folders gives the example bank's simulator one account it refuses, so one credit is returned.

const RUNTIME = path.resolve(STUDIO, '..', 'runtime');
const BANKS = path.resolve(STUDIO, '..', 'content-configurations', 'banks');
const TENANT = 'example-bank';
const REFUSED = 'AE570330000000000000002'; // the first credit of samples/wps-pif-ssm.pif
const DOTNET_ENV = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };
const BENCH = (core: string) => [
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-inward.v1', scenario: 'payment-file-returns-accepted' },
  core === 'runtime' ? { port: 'p.port.core-banking.v1', simulator: 'runtime', scenario: '' } : { port: 'p.port.core-banking.v1', simulator: 'p.sim.core-banking.v1', scenario: 'reject-first-then-accept' },
  { port: 'p.port.sanctions.v1', simulator: 'p.sim.sanctions.v1', scenario: 'accept' },
  { port: 'p.port.fraud.v1', simulator: 'p.sim.fraud.v1', scenario: 'accept' },
  { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'reject' },
];

let native: ChildProcess;
let nativeUrl = '';
let onRuntime: Server;
let onStudio: Server;
const banks = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-inward-native-'));
const freePort = () => new Promise<number>((resolve) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as any).port; s.close(() => resolve(p)); }); });

beforeAll(async () => {
  fs.cpSync(BANKS, banks, { recursive: true });
  const cb = path.join(banks, TENANT, 'core-banking.yaml');
  fs.writeFileSync(cb, fs.readFileSync(cb, 'utf8').replace('      openingBalance: "1000000"\n', `      openingBalance: "1000000"\n      rejectAccount: ${REFUSED}\n`));
  const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: path.join(RUNTIME, 'src', 'TransactHub.Native'), encoding: 'utf8', env: DOTNET_ENV });
  if (b.status !== 0) throw new Error(`dotnet build failed:\n${b.stdout}\n${b.stderr}`);
  nativeUrl = `http://127.0.0.1:${await freePort()}`;
  native = spawn('dotnet', [path.join(RUNTIME, 'src', 'TransactHub.Native', 'bin', 'Release', 'net10.0', 'TransactHub.Native.dll')], { env: { ...DOTNET_ENV, TH_NATIVE_URLS: nativeUrl, TH_BANKS_DIR: banks }, stdio: 'ignore' });
  for (let i = 0; i < 120; i++) { try { if ((await fetch(`${nativeUrl}/health`)).ok) break; } catch { /* not yet */ } await new Promise((r) => setTimeout(r, 500)); }
  onRuntime = await startServer({ TH_NATIVE_HOST: nativeUrl, TH_NATIVE_ACTIVITIES: '*' });
  onStudio = await startServer();
}, 600000);
afterAll(() => {
  onRuntime?.stop();
  onStudio?.stop();
  native?.kill();
  fs.rmSync(banks, { recursive: true, force: true });
});

const run = (s: Server, core: string) => client(s.url).post('/api/bench/run', {
  target: 'p.wf.inward.v1', tenant: TENANT, inbound: 'samples/wps-pif-ssm.pif', message: 'uae.wps.pif', bench: BENCH(core), evaluationDate: '2026-11-17',
});

describe('the WPS inward flows on the runtime (plan P2)', () => {
  it('maps, confirms, credits, reports and returns with the source\'s parsers and builders, and posts the return through core banking', async () => {
    const r = await run(onRuntime, 'runtime');
    expect(r.status, JSON.stringify(r.data).slice(0, 800)).toBe(200);
    const leg = r.data.instances?.find((i: any) => i.workflow === 'uae.wps.wf.agent-payment-in.v2') ?? r.data.record?.instances?.find((i: any) => i.workflow === 'uae.wps.wf.agent-payment-in.v2');
    const trace: string[] = leg?.trace ?? r.data.trace;
    expect(trace, JSON.stringify(r.data).slice(0, 1500)).toEqual(expect.arrayContaining(['posting-window:open', 'credit[0]:rejected', 'credit[1]:posted', 'report-disbursement:accepted', 'returns:returns', 'render-returns:mapped', 'return-credits:accepted', 'record-return:posted']));
    const health = (await (await fetch(`${nativeUrl}/health`)).json()).served;
    for (const a of ['uae.wps.map.pif-in.v1', 'uae.wps.map.prc-out.v2', 'uae.wps.map.dif-out.v2', 'uae.wps.map.rfa-out.v1', 'uae.wps.map.ack-in.v1', 'p.decide.posting-window.v1', 'p.post.accounting.v1', 'uae.wps.post.return.v1']) expect(health[a], a).toBeGreaterThan(0);
    // The three files the agent wrote are the host's, by name.
    const files = ((await (await fetch(`${nativeUrl}/files`)).json()) as any[]).map((f) => f.name);
    expect(files.some((n) => /^000\d{12}\.PRC$/.test(n)), files.join(',')).toBe(true);
    expect(files.some((n) => n.endsWith('.DIF'))).toBe(true);
    expect(files.some((n) => n.endsWith('.RFA'))).toBe(true);
    // The return moved the refused credit's amount, 1500.00, from suspense to settlement.
    const postings = ((await (await fetch(`${nativeUrl}/postings`)).json()) as any[]).filter((p) => p.tenant === TENANT).flatMap((p) => p.batches);
    const ret = postings.find((b: any) => (b.Reference ?? b.reference).endsWith(':return'));
    expect(ret, JSON.stringify(postings).slice(0, 800)).toBeDefined();
    expect(ret.legs.map((l: any) => [l.Kind ?? l.kind, l.Account ?? l.account, Number(l.Amount ?? l.amount)])).toEqual([['debit', 'GL-SUSPENSE', 1500], ['credit', 'GL-SETTLEMENT', 1500]]);
  }, 300000);

  it('gives the same business trace as the studio\'s own scripts and simulators', async () => {
    const native = await run(onRuntime, 'runtime');
    const studio = await run(onStudio, 'simulator');
    expect(studio.status).toBe(200);
    expect(native.data.trace).toEqual(studio.data.trace);
    expect(native.data.outcome).toBe(studio.data.outcome);
  }, 300000);
});
