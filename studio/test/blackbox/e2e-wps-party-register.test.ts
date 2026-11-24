import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as net from 'node:net';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, startServer, STUDIO, type Server } from './server.js';
import { devModules } from './native-module.js';

// End to end, black box (R-12-27, R-05-62, R-05-63): a WPS salary file matched against the bank's party register on the runtime.
// The store is PostgreSQL in a container of this run's own; the operations console's API migrates it and is where a register maker
// proposes the file's employer and a bank approver consents. The native host reads the register from the store (the example bank's
// binding of p.port.party-register.v1, adapter `register`), and WPS feeds the source's SifUploadRules.MatchCorporate with it. The
// file is refused while its employer is not registered, still refused while the registration only waits for consent, and goes on
// to WPS once the approver has consented. Synthetic corporate and file only.

const RUNTIME = path.resolve(STUDIO, '..', 'runtime');
const REPO = path.resolve(STUDIO, '..');
const TENANT = 'example-bank';
const PASSWORD = 'Th-Test-Only-1234';
const DB = `th-e2e-party-register-pg-${process.pid}`;
const DOTNET_ENV = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };
const BENCH = [
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
  { port: 'p.port.encryption.v1', simulator: 'p.sim.encryption.v1', scenario: 'accept' },
  { port: 'uae.wps.port.labour-ministry.v1', simulator: 'uae.wps.sim.labour-ministry.v1', scenario: 'accept' },
  // Given to the runtime: the native host answers p.enrich.party-register.v1 from the register.
  { port: 'p.port.party-register.v1', simulator: 'p.sim.party-register.v1', scenario: 'not-registered' },
];
const procs: ChildProcess[] = [];
const logs: Record<string, string> = {};
let studio: Server;
let opsUrl = '';
let nativeUrl = '';

const docker = (...a: string[]) => spawnSync('docker', a, { encoding: 'utf8' });
const freePort = () => new Promise<number>((resolve) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as any).port; s.close(() => resolve(p)); }); });

function start(name: string, dll: string, env: Record<string, string>) {
  logs[name] = '';
  const p = spawn('dotnet', [dll], { cwd: REPO, env: { ...DOTNET_ENV, ...env } as NodeJS.ProcessEnv });
  p.stdout?.on('data', (d) => (logs[name] += d));
  p.stderr?.on('data', (d) => (logs[name] += d));
  procs.push(p);
}

async function until<T>(what: string, f: () => Promise<T | undefined | null | false>, ms = 120000): Promise<T> {
  const end = Date.now() + ms;
  for (;;) {
    try { const v = await f(); if (v) return v; } catch { /* not yet */ }
    if (Date.now() > end) throw new Error(`timed out waiting for ${what}\n${Object.entries(logs).map(([k, v]) => `--- ${k}\n${v.slice(-2000)}`).join('\n')}`);
    await new Promise((r) => setTimeout(r, 500));
  }
}

const as = (sub: string, role: string) => ({ 'content-type': 'application/json', 'X-TH-Dev-Identity': JSON.stringify({ sub, name: sub, roles: [`${TENANT}:${role}`] }) });

beforeAll(async () => {
  for (const project of ['src/TransactHub.Operations.Api', 'src/TransactHub.Native']) {
    const b = spawnSync('dotnet', ['build', project, '-c', 'Release', '--nologo', '-v', 'q'], { cwd: RUNTIME, encoding: 'utf8', env: DOTNET_ENV });
    if (b.status !== 0) throw new Error(`dotnet build ${project} failed:\n${b.stdout}\n${b.stderr}`);
  }
  const created = docker('run', '-d', '--name', DB, '-e', `POSTGRES_PASSWORD=${PASSWORD}`, '-p', '127.0.0.1::5432', 'postgres:16-alpine');
  if (created.status !== 0) throw new Error(created.stderr);
  await until('PostgreSQL', async () => docker('exec', DB, 'pg_isready', '-U', 'postgres').status === 0 && docker('exec', DB, 'psql', '-U', 'postgres', '-c', 'select 1').status === 0);
  const port = docker('port', DB, '5432').stdout.split('\n')[0].split(':').pop()!.trim();
  const connection = `Host=127.0.0.1;Port=${port};Username=postgres;Password=${PASSWORD};Database=th_e2e_party_register`;
  opsUrl = `http://127.0.0.1:${await freePort()}`;
  start('ops', path.join(RUNTIME, 'src/TransactHub.Operations.Api/bin/Release/net10.0/TransactHub.Operations.Api.dll'), {
    ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: opsUrl, TH_AUTH_DEV: '1', Store__Provider: 'PostgreSql', Store__Connection: connection,
    Operations__Views: path.join(REPO, 'content-configurations', 'artefacts'),
  });
  await until('the console API', async () => (await fetch(`${opsUrl}/ops/api/health`)).ok, 300000);
  nativeUrl = `http://127.0.0.1:${await freePort()}`;
  start('native', path.join(RUNTIME, 'src/TransactHub.Native/bin/Release/net10.0/TransactHub.Native.dll'), {
    TH_NATIVE_URLS: nativeUrl, TH_BANKS_DIR: path.join(REPO, 'content-configurations', 'banks'), TH_STORE_PROVIDER: 'PostgreSql', TH_STORE_CONNECTION: connection,
    // WPS's match of the registered corporate is the WPS module's, outside any distribution here (R-09-122).
    TH_NATIVE_DEV_MODULES: devModules(),
  });
  await until('the native host', async () => (await fetch(`${nativeUrl}/health`)).ok);
  studio = await startServer({ TH_NATIVE_HOST: nativeUrl, TH_NATIVE_ACTIVITIES: 'p.enrich.party-register.v1' });
}, 900000);

afterAll(() => {
  studio?.stop();
  for (const p of procs) if (p.exitCode === null) p.kill('SIGKILL');
  docker('rm', '-f', '-v', DB);
});

const run = () => client(studio.url).post('/api/bench/run', {
  target: 'uae.wps.wf.salary-file-out.v1', tenant: TENANT, inbound: 'samples/wps-sif-valid.sif', bench: BENCH, evaluationDate: '2026-11-30',
});
const traceOf = (r: any): string[] => r.data.trace ?? r.data.record?.trace ?? [];

describe('a WPS salary file against the bank\'s party register, on the runtime (R-12-27)', () => {
  it('refuses the file of an unregistered employer, still refuses it while the registration waits, and sends it once consented', async () => {
    const refused = await run();
    expect(refused.status, JSON.stringify(refused.data).slice(0, 800)).toBe(200);
    expect(traceOf(refused), JSON.stringify(refused.data).slice(0, 1500)).toEqual(['business-date:set', 'cut-off:taken', 'corporate:not-registered', 'end:rejected']);
    expect(refused.data.outcome).toBe('rejected');
    // The runtime answered, not the simulator (its scenario says not-registered too, so the served count proves it).
    expect((await (await fetch(`${nativeUrl}/health`)).json()).served['p.enrich.party-register.v1']).toBeGreaterThan(0);

    // The employer is proposed by a register maker: not in force until a bank approver consents.
    const corporate = {
      corporateId: 'C-E2E-0001', name: 'Example Trading LLC', active: true, wpsRegistrationFormHeld: true, establishmentId: '0000001234567',
      accounts: ['AE070331234567890123456'], cif: 'CIF000111',
    };
    const proposed = await fetch(`${opsUrl}/ops/api/registers/corporate/changes`, { method: 'POST', headers: as('reg.maker', 'RegisterMaker'), body: JSON.stringify({ records: [{ fields: corporate }] }) });
    expect(proposed.status, await proposed.clone().text()).toBe(201);
    const change = (await proposed.json()).id;
    const waiting = await run();
    expect(traceOf(waiting)).toEqual(['business-date:set', 'cut-off:taken', 'corporate:not-registered', 'end:rejected']);
    expect((await fetch(`${opsUrl}/ops/api/registers/changes/${change}/decision`, { method: 'POST', headers: as('reg.maker', 'BankApprover'), body: JSON.stringify({ approve: true }) })).status).toBe(403);
    const consented = await fetch(`${opsUrl}/ops/api/registers/changes/${change}/decision`, { method: 'POST', headers: as('reg.approver', 'BankApprover'), body: JSON.stringify({ approve: true }) });
    expect(consented.status).toBe(200);

    // Registered: the file is matched with the source's rules and goes on to WPS.
    const sent = await run();
    expect(traceOf(sent), JSON.stringify(sent.data).slice(0, 1500)).toEqual(expect.arrayContaining(['business-date:set', 'corporate:registered', 'employer-status:found', 'file-checks:pass', 'submit:accepted', 'end:settled']));
    expect(sent.data.outcome).toBe('settled');
  }, 600000);
});
