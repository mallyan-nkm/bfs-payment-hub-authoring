import { spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, startServer, STUDIO, type Server } from './server.js';
import { unzip } from '../../src/export/zip.js';

// End to end, black box: the WPS salary file on Flowable in production mode (Issues I-53 to I-55). The
// Flowable REST server runs with its async executor on, in real time: it runs its own jobs, fires its timers,
// retries a failed job after its retry timeout and hands out a job whose lock has expired. The runtime's
// binding (runtime/tools/TransactHub.FlowableHarness, TH_FLOWABLE_MODE=production) is a pure external worker,
// with the payloads in the store. Real waits are shortened by parameters: the acknowledgement wait is the
// bank's parameter (R-05-47), set here to seconds, and the simulator's reply delay is scaled (as the stub's
// --time-scale). The server's polling is shortened to a second. A run in virtual mode, on a server with its
// executor off, gives the trace each production run must give.

const TENANT = 'example-bank';
const SAMPLE = 'samples/wps-sif-valid.sif';
const MESSAGE = 'uae.wps.sif';
const PLATFORM = 'p.wf.corporate.v1';
const ACK_WAIT = 'uae.wps.exchange.submit-salary-file.v1#ackWait';
const ACK_CALENDAR = 'uae.wps.exchange.submit-salary-file.v1#ackWaitCalendar';
const bench = (wps: string) => [
  ...['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: wps },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
];
const SETTLED = ['validate:pass', 'duplicate:unique', 'hook:done', 'sanctions:clear', 'fraud:clear', 'join:all', 'limits:pass', 'reserve:reserved', 'route:decided', 'hook:continue', 'leg:settled', 'post:posted', 'verify:verified', 'finally:notify', 'finally:publish', 'hook:done', 'end:settled'];
const TOOLS = path.resolve(STUDIO, '..', 'runtime', 'tools');
const FLOWABLE_IMAGE = 'flowable/flowable-rest:7.1.0@sha256:b7ae287502cd5c243b577a0ca2894172e1c18cd75aea4fd67dc7d58f1b40db44';
const FLOWABLE_USER = 'th-binding';
const FLOWABLE_PASSWORD = 'th-test-only';
const PASSWORD = 'Th-Test-Only-1234';
// Production mode: a simulator's ten-minute reply comes in six seconds; a failed job is handed out again after a second.
const PRODUCTION = { TH_FLOWABLE_MODE: 'production', TH_FLOWABLE_SIM_SCALE: '0.01', TH_FLOWABLE_RETRY_WAIT: 'PT1S', TH_FLOWABLE_POLL: 'PT0.1S', TH_FLOWABLE_MAX_WAIT: 'PT8M' };

let server: Server;
let api: ReturnType<typeof client>;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-wps-flowable-live-'));
const docker = (...a: string[]) => spawnSync('docker', a, { encoding: 'utf8' });
const started: string[] = [];
const databases: Record<string, { port: string }> = {};
const flowables: Record<string, Record<string, string>> = {};

beforeAll(async () => {
  const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: path.join(TOOLS, 'TransactHub.FlowableHarness'), encoding: 'utf8', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
  if (b.status !== 0) throw new Error(`dotnet build failed:\n${b.stdout}\n${b.stderr}`);
  server = await startServer();
  api = client(server.url);
}, 900000);
afterAll(() => {
  server?.stop();
  for (const c of started) docker('rm', '-f', '-v', c);
  fs.rmSync(work, { recursive: true, force: true });
});

async function database(kind: 'PostgreSql' | 'SqlServer', db: string) {
  if (!databases[kind]) {
    const name = `th-e2e-live-${kind.toLowerCase()}-${process.pid}`;
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
    databases[kind] = { port: docker('port', name).stdout.split('\n')[0].split(':').pop()!.trim() };
  }
  const { port } = databases[kind];
  const connection = kind === 'PostgreSql'
    ? `Host=127.0.0.1;Port=${port};Username=postgres;Password=${PASSWORD};Database=${db}`
    : `Server=127.0.0.1,${port};User Id=sa;Password=${PASSWORD};TrustServerCertificate=true;Database=${db}`;
  return { TH_STORE_PROVIDER: kind, TH_STORE_CONNECTION: connection };
}

/** A Flowable REST server: its async executor on (production, polling every second) or off (virtual). */
async function flowableServer(mode: 'production' | 'virtual') {
  if (flowables[mode]) return flowables[mode];
  const name = `th-e2e-live-flowable-${mode}-${process.pid}`;
  const executor = mode === 'production'
    ? ['-e', 'FLOWABLE_ASYNC_EXECUTOR_ACTIVATE=true', '-e', 'FLOWABLE_PROCESS_ASYNC_EXECUTOR_DEFAULT_TIMER_JOB_ACQUIRE_WAIT_TIME=PT1S', '-e', 'FLOWABLE_PROCESS_ASYNC_EXECUTOR_DEFAULT_ASYNC_JOB_ACQUIRE_WAIT_TIME=PT1S', '-e', 'FLOWABLE_PROCESS_ASYNC_EXECUTOR_RESET_EXPIRED_JOBS_INTERVAL=PT1S']
    : ['-e', 'FLOWABLE_ASYNC_EXECUTOR_ACTIVATE=false', '-e', 'FLOWABLE_ASYNC_HISTORY_EXECUTOR_ACTIVATE=false'];
  const run = docker('run', '-d', '--name', name, ...executor, '-e', 'FLOWABLE_REST_APP_CREATE_DEMO_DEFINITIONS=false',
    '-e', `FLOWABLE_REST_APP_ADMIN_USER_ID=${FLOWABLE_USER}`, '-e', `FLOWABLE_REST_APP_ADMIN_PASSWORD=${FLOWABLE_PASSWORD}`, '-p', '127.0.0.1::8080', FLOWABLE_IMAGE);
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
  return (flowables[mode] = { TH_FLOWABLE_URL: url, TH_FLOWABLE_USER: FLOWABLE_USER, TH_FLOWABLE_PASSWORD: FLOWABLE_PASSWORD });
}

let zip: string | undefined;
async function pkg() {
  if (zip) return zip;
  const z = await api.get(`/api/export/download?workflows=${PLATFORM}&target=flowable&withSimulators=true&tenant=${TENANT}`);
  expect(z.status).toBe(200);
  zip = path.join(work, 'corporate-flowable.zip');
  fs.writeFileSync(zip, z.data);
  return zip;
}

async function deploy(env: Record<string, string>, wps: string, values: Record<string, string>, sample = path.join(STUDIO, 'fixtures', SAMPLE)) {
  const args = ['src/cli.ts', 'deploy', await pkg(), '--workflow', PLATFORM, '--message', MESSAGE, '--inbound', sample, '--tenant', TENANT,
    ...bench(wps).flatMap((b) => ['--bench', `${b.port}=${b.simulator}:${b.scenario}`]), ...Object.entries(values).flatMap(([k, v]) => ['--value', `${k}=${v}`])];
  const t0 = Date.now();
  const out = await new Promise<string>((resolve) => {
    let text = '';
    const child = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), args, { cwd: STUDIO, env: { ...process.env, ...env } });
    const timer = setTimeout(() => child.kill('SIGKILL'), 600000);
    child.stdout.on('data', (d) => (text += d));
    child.stderr.on('data', (d) => (text += d));
    child.on('close', () => { clearTimeout(timer); resolve(text); });
  });
  return { out, ms: Date.now() - t0, trace: /^trace  (.*)$/m.exec(out)?.[1].split(' | ') ?? [], outcome: /^outcome (.*)$/m.exec(out)?.[1] };
}

/** A valid synthetic UAE IBAN for bank code 033 (ISO 13616 check digits). */
function iban(account: number): string {
  const bban = `033${String(account).padStart(16, '0')}`;
  let mod = 0;
  for (const ch of `${bban}101400`) mod = (mod * 10 + Number(ch)) % 97;
  return `AE${String(98 - mod).padStart(2, '0')}${bban}`;
}
/** A synthetic salary file of n lines of 100.00 each, for one employer. */
function salaryFile(n: number): string {
  const edr = Array.from({ length: n }, (_, i) => `EDR,${String(91000000000000 + i).padStart(14, '0')},203320101,${iban(7100000000000000 + i)},2026-10-01,2026-10-31,31,100.00,0.00,0`);
  return [`# file: 0000001234567261117084000.sif`, `SCR,0000001234567,302620122,2026-11-17,0840,102026,${n},${(n * 100).toFixed(2)},AED,REF-MANY`, ...edr].join('\n') + '\n';
}

const storeLine = (kind: string) => new RegExp(`^store: ${kind} holds 1 bulk\\(s\\), 1 payment\\(s\\), \\d+ part\\(ies\\); [1-9]\\d* payload\\(s\\) rebuilt from their facts, chains unbroken, all match the run$`, 'm');

describe('the WPS salary file on Flowable in production mode: the async executor on, real time', () => {
  for (const kind of ['PostgreSql', 'SqlServer'] as const) {
    it(`on ${kind}: settles in real time, the acknowledgement wait placed by the binding from the bank's parameter`, async () => {
      const env = { ...(await database(kind, 'th_flowable_live')), ...(await flowableServer('production')), ...PRODUCTION };
      const d = await deploy(env, 'accept', { [ACK_WAIT]: 'PT2M', [ACK_CALENDAR]: 'elapsed' });
      expect(d.outcome, d.out.slice(-3000)).toBe('settled');
      expect(d.trace).toEqual(SETTLED);
      expect(d.out).toMatch(/^\[engine\] production mode: the server's async executor runs the jobs and fires the timers in real time/m);
      // The await's timer was written with the policy's day on the business calendar; the binding gave it the bank's two minutes.
      // Business time is the wall clock from 06:00 on the evaluation date, so the minute the wait starts is the run's own (on SQL
      // Server the store's journal makes the steps before it slower, Issue I-169): what holds is that the timer is placed two
      // minutes after the wait began, one business day minus two minutes before the day the BPMN wrote.
      const placed = /^\[engine\] timer submit\.await-ack_timeout placed at (2026-11-17T\S+Z) \(business time\), .* written (2026-11-18T\S+Z)/m.exec(d.out);
      expect(placed, d.out.slice(-3000)).not.toBeNull();
      expect(Math.abs(Date.parse(placed![2]) - Date.parse(placed![1]) - (86400000 - 120000))).toBeLessThan(2000);
      expect(d.out, d.out.slice(-3000)).toMatch(storeLine(kind));
      // The reply came after its scaled six seconds, in real time.
      expect(d.ms).toBeGreaterThan(6000);
    }, 900000);
  }

  it('on PostgreSql: an acknowledgement that never comes times out on a real timer, with the trace of the virtual run', async () => {
    const values = { [ACK_WAIT]: 'PT15S', [ACK_CALENDAR]: 'elapsed' };
    const db = await database('PostgreSql', 'th_flowable_live_timeout');
    const virtual = await deploy({ ...(await database('PostgreSql', 'th_flowable_virtual_timeout')), ...(await flowableServer('virtual')) }, 'no-reply', values);
    expect(virtual.trace, virtual.out.slice(-3000)).toContain('leg:pending');
    const live = await deploy({ ...db, ...(await flowableServer('production')), ...PRODUCTION }, 'no-reply', values);
    expect(live.trace, live.out.slice(-3000)).toEqual(virtual.trace);
    expect(live.outcome).toBe(virtual.outcome);
    // The fifteen seconds passed on the server's clock.
    expect(live.ms).toBeGreaterThan(15000);
    expect(live.out).toMatch(storeLine('PostgreSql'));
  }, 900000);

  it('on PostgreSql: a job held by a worker that crashed comes back when its lock expires, and the payment settles', async () => {
    const env = { ...(await database('PostgreSql', 'th_flowable_live_crash')), ...(await flowableServer('production')), ...PRODUCTION, TH_FLOWABLE_LOCK: 'PT5S', TH_FLOWABLE_FAULT: 'crash:post' };
    const d = await deploy(env, 'accept', { [ACK_WAIT]: 'PT2M', [ACK_CALENDAR]: 'elapsed' });
    expect(d.outcome, d.out.slice(-3000)).toBe('settled');
    expect(d.trace).toEqual(SETTLED);
    expect(d.out).toMatch(/^\[engine\] post: the worker stopped holding job \S+ \(TH_FLOWABLE_FAULT\); its lock expires after PT5S/m);
    expect(d.out).toMatch(storeLine('PostgreSql'));
  }, 900000);

  // Issue I-89, R-09-65: the binding's host is killed in the middle of the payment (after the posting's answer is recorded, before
  // it completes the job) and started again with the same run key; it goes on from its facts in the store and the file settles.
  it('on PostgreSql: a binding that stops in the middle of the payment is started again and goes on from the store', async () => {
    const env = { ...(await database('PostgreSql', 'th_flowable_live_restart')), ...(await flowableServer('production')), ...PRODUCTION, TH_FLOWABLE_FAULT: 'exit:post' };
    const d = await deploy(env, 'accept', { [ACK_WAIT]: 'PT2M', [ACK_CALENDAR]: 'elapsed' });
    expect(d.outcome, d.out.slice(-3000)).toBe('settled');
    expect(d.trace).toEqual(SETTLED);
    expect(d.out).toMatch(/^\[engine\] the binding stops at post \(TH_FLOWABLE_FAULT\)/m);
    expect(d.out).toMatch(/engine: the Flowable binding stopped \(exit 75\); started again with run key deploy-\S+ \(restart 1\)/);
    expect(d.out).toMatch(/^\[engine\] restarted \(restart 1\): run deploy-/m);
    expect(d.out).toMatch(/^\[engine\] post: job \S+ handed out again after the restart; answered from the journal, not called again$/m);
    expect(d.out).toMatch(storeLine('PostgreSql'));
  }, 900000);

  // Issue I-107, R-09-45: a file of many lines on Flowable, its record checks side by side in a chunk of the binding's concurrency (64).
  it('on PostgreSql: a file of forty lines runs its lines side by side and settles with the trace of every other engine', async () => {
    const files = Object.fromEntries(unzip(fs.readFileSync(await pkg())).map((e) => [e.name, e.content.toString('utf8')]));
    const leg = Object.entries(files).find(([n]) => n.startsWith('bpmn/uae.wps.wf.salary-file-out.v1@'))?.[1] ?? '';
    expect(leg).toMatch(/<multiInstanceLoopCharacteristics isSequential="false"><loopCardinality>\$\{execution\.getVariable\('th_count_items'\) - execution\.getVariable\('th_chunk'\) \* 64 &lt; 64/);
    expect(leg).toContain('kind="itemStop"');
    const sample = path.join(work, 'wps-sif-forty-lines.sif');
    fs.writeFileSync(sample, salaryFile(40));
    const env = { ...(await database('PostgreSql', 'th_flowable_live_many')), ...(await flowableServer('production')), ...PRODUCTION };
    const d = await deploy(env, 'accept', { [ACK_WAIT]: 'PT2M', [ACK_CALENDAR]: 'elapsed' }, sample);
    expect(d.outcome, d.out.slice(-3000)).toBe('settled');
    expect(d.trace).toEqual(SETTLED);
    expect(d.out).toMatch(/^store: PostgreSql holds 1 bulk\(s\), 40 payment\(s\), \d+ part\(ies\); [1-9]\d* payload\(s\) rebuilt from their facts, chains unbroken, all match the run$/m);
  }, 900000);

  it('on SqlServer: a step whose worker never answers is retried by Flowable, then is a dead letter the binding surfaces', async () => {
    const env = { ...(await database('SqlServer', 'th_flowable_live_dead')), ...(await flowableServer('production')), ...PRODUCTION, TH_FLOWABLE_FAULT: 'down:post' };
    const d = await deploy(env, 'accept', { [ACK_WAIT]: 'PT2M', [ACK_CALENDAR]: 'elapsed' });
    expect(d.out).toMatch(/^\[engine\] post: the worker of post does not answer \(TH_FLOWABLE_FAULT\); failed with 2 retries left, Flowable hands it out again after PT1S$/m);
    expect(d.out).toMatch(/^\[engine\] post: .*failed with no retries left: a dead letter$/m);
    expect(d.out).toMatch(/^\[engine\] note: dead letter: the job of post \(\S+\) in p\.wf\.corporate\.v1 has no retries left: the worker of post does not answer/m);
    expect(d.outcome).toMatch(/\(dead letter at post: the worker of post does not answer/);
    // The payment stopped where the dead letter is: posted is never reached.
    expect(d.trace).toContain('leg:settled');
    expect(d.trace).not.toContain('post:posted');
  }, 900000);
});
