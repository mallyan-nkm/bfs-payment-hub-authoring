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

let server: Server;
let api: ReturnType<typeof client>;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-wps-temporal-'));

beforeAll(async () => {
  if (!fs.existsSync(path.join(TEST_SERVER, 'classpath.txt'))) spawnSync(path.join(TEST_SERVER, 'setup.sh'), { encoding: 'utf8' });
  const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: HARNESS, encoding: 'utf8', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
  if (b.status !== 0) throw new Error(`dotnet build failed:\n${b.stdout}\n${b.stderr}`);
  server = await startServer();
  api = client(server.url);
}, 600000);
afterAll(() => server?.stop());

async function deploy(label: string, sample = SAMPLE, extra: any[] = []) {
  const z = await api.get(`/api/export/download?workflows=${PLATFORM}&target=temporal&withSimulators=true&tenant=${TENANT}`);
  expect(z.status).toBe(200);
  const file = path.join(work, `${label}.zip`);
  fs.writeFileSync(file, z.data);
  const args = ['src/cli.ts', 'deploy', file, '--workflow', PLATFORM, '--message', MESSAGE, '--inbound', path.join(STUDIO, 'fixtures', sample), '--tenant', TENANT, ...[...BENCH, ...extra].flatMap((b) => ['--bench', `${b.port}=${b.simulator}:${b.scenario}`])];
  // Asynchronous, so the studio server's kept-alive connections stay open while Temporal runs.
  const out = await new Promise<string>((resolve) => {
    let text = '';
    const child = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), args, { cwd: STUDIO, env: { ...process.env, TH_DATA_ROOT: '' } });
    const timer = setTimeout(() => child.kill('SIGKILL'), 600000);
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
  }, 600000);

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
    }, 600000);
  }
});
