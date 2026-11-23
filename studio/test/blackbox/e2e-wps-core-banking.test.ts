import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as net from 'node:net';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, startServer, STUDIO, type Server } from './server.js';

// End to end, black box (plan P2, R-08-15): the WPS payment file, with the bank as the agent, on the studio's
// interpreter, its core banking given to the runtime. The runtime's native host (TH_BANKS_DIR = the banks' folders)
// answers each credit through the tenant's core-banking binding in force on the payment's day: the example bank is on
// the simulator adapter until 2026-12-01 and on its gateway (an .invalid host) from then. The accounts are the
// factory's placeholder accounts and the payment file's synthetic IBANs.

const RUNTIME = path.resolve(STUDIO, '..', 'runtime');
const BANKS = path.resolve(STUDIO, '..', 'content-configurations', 'banks');
const TENANT = 'example-bank';
const DOTNET_ENV = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };
const BENCH = [
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-inward.v1', scenario: 'payment-file' },
  { port: 'p.port.core-banking.v1', simulator: 'runtime', scenario: '' },
  { port: 'p.port.sanctions.v1', simulator: 'p.sim.sanctions.v1', scenario: 'accept' },
  { port: 'p.port.fraud.v1', simulator: 'p.sim.fraud.v1', scenario: 'accept' },
];

let server: Server;
let api: ReturnType<typeof client>;
let native: ChildProcess;
let nativeUrl = '';
const freePort = () => new Promise<number>((resolve) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as any).port; s.close(() => resolve(p)); }); });

beforeAll(async () => {
  const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: path.join(RUNTIME, 'src', 'TransactHub.Native'), encoding: 'utf8', env: DOTNET_ENV });
  if (b.status !== 0) throw new Error(`dotnet build failed:\n${b.stdout}\n${b.stderr}`);
  nativeUrl = `http://127.0.0.1:${await freePort()}`;
  native = spawn('dotnet', [path.join(RUNTIME, 'src', 'TransactHub.Native', 'bin', 'Release', 'net10.0', 'TransactHub.Native.dll')], { env: { ...DOTNET_ENV, TH_NATIVE_URLS: nativeUrl, TH_BANKS_DIR: BANKS }, stdio: 'ignore' });
  for (let i = 0; i < 120; i++) { try { if ((await fetch(`${nativeUrl}/health`)).ok) break; } catch { /* not yet */ } await new Promise((r) => setTimeout(r, 500)); }
  server = await startServer({ TH_NATIVE_HOST: nativeUrl });
  api = client(server.url);
}, 600000);
afterAll(() => {
  server?.stop();
  native?.kill();
});

const run = (evaluationDate: string) =>
  api.post('/api/bench/run', { target: 'p.wf.inward.v2', tenant: TENANT, inbound: 'samples/wps-pif-valid.pif', message: 'uae.wps.pif', bench: BENCH, evaluationDate });
const postings = async () => ((await (await fetch(`${nativeUrl}/postings`)).json()) as any[]).filter((p) => p.tenant === TENANT);

describe('the payment file credits post through the bank\'s core-banking binding', () => {
  it('before the switch: every credit is posted by the simulator adapter, settlement to the employee, under the template\'s code', async () => {
    const r = await run('2026-11-17');
    expect(r.status, JSON.stringify(r.data).slice(0, 600)).toBe(200);
    expect(r.data.trace, r.data.trace?.join(' | ')).toContain('end:settled');
    const sim = await postings();
    expect(sim).toHaveLength(1);
    expect(sim[0].adapter).toBe('simulator');
    const batches = sim[0].batches;
    expect(batches).toHaveLength(2); // the file's two credits
    for (const b of batches) {
      expect(b.Reference ?? b.reference).toMatch(/:AE\d{21}$/);
      const [debit, credit] = b.legs.map((l: any) => ({ kind: l.Kind ?? l.kind, account: l.Account ?? l.account, code: l.TxnCode ?? l.txnCode }));
      expect(debit).toEqual({ kind: 'debit', account: 'GL-SETTLEMENT', code: 'PAY' });
      expect(credit.kind).toBe('credit');
      expect(credit.account).toMatch(/^AE\d{21}$/);
    }
    const served = (await (await fetch(`${nativeUrl}/health`)).json()).served;
    expect(served['p.post.accounting.v1']).toBe(2);
  }, 300000);

  it('from the switch: the same file goes to the gateway, which is not reachable, so each credit is unknown and nothing more is posted on the simulator', async () => {
    const before = (await postings())[0].batches.length;
    const r = await run('2026-12-15');
    expect(r.status, JSON.stringify(r.data).slice(0, 600)).toBe(200);
    expect(r.data.trace.join(' | ')).not.toContain('credit:posted');
    expect((await postings())[0].batches.length).toBe(before);
    // The gateway adapter was built for the binding from 2026-12-01; no credit was guessed.
    const served = (await (await fetch(`${nativeUrl}/health`)).json()).served;
    expect(served['p.post.accounting.v1']).toBe(4);
  }, 300000);
});
