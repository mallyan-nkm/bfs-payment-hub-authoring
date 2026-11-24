import { spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, startServer, STUDIO, type Server } from './server.js';

// The WPS parity suite on DTFx and Elsa (plan P3): the scenarios e2e-wps-temporal runs on Temporal, run on
// each of the other two runtime engines. The factory WPS salary file is exported for the bank with the
// engine's binding and deployed through the deploy runner (src/deploy/deploy.ts): the front door, the
// corporate payment workflow, the WPS leg in its slot, each activity on the worker of its scope's queue.
// Each way the payment can end gives the same trace and outcome as the studio's own interpreter (R-09-07).
// Without TH_STORE_PROVIDER the deploy runner runs the studio's engine harnesses (studio/engines/{dtfx,elsa});
// with it, the runtime's builds (runtime/tools/TransactHub.{Dtfx,Elsa}Harness), and the store line is checked.
// The source's Central Bank SIF writer on these engines is in e2e-wps-source-slice (steps 6 and 7).
//
// TH_PARITY_ENGINES=dtfx (or elsa) runs one engine only, as CI's matrix does.

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
const ENGINES = (process.env.TH_PARITY_ENGINES ?? 'dtfx,elsa').split(',').map((e) => e.trim()).filter(Boolean) as ('dtfx' | 'elsa')[];
const HARNESSES: Record<string, string> = { dtfx: path.join(STUDIO, 'engines', 'dtfx', 'harness'), elsa: path.join(STUDIO, 'engines', 'elsa', 'harness') };
const RUNTIME_HARNESSES: Record<string, string> = { dtfx: path.resolve(STUDIO, '..', 'runtime', 'tools', 'TransactHub.DtfxHarness'), elsa: path.resolve(STUDIO, '..', 'runtime', 'tools', 'TransactHub.ElsaHarness') };
const ON_STORE = !!process.env.TH_STORE_PROVIDER;
const RUN_MS = 600000;

let server: Server;
let api: ReturnType<typeof client>;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-wps-parity-'));

beforeAll(async () => {
  for (const e of ENGINES) {
    const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: (ON_STORE ? RUNTIME_HARNESSES : HARNESSES)[e], encoding: 'utf8', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
    if (b.status !== 0) throw new Error(`dotnet build of the ${e} harness failed:\n${b.stdout}\n${b.stderr}`);
  }
  server = await startServer();
  api = client(server.url);
}, 900000);
afterAll(() => {
  server?.stop();
  fs.rmSync(work, { recursive: true, force: true });
});

async function deploy(engine: string, label: string, sample = SAMPLE, extra: any[] = [], flow = { platform: PLATFORM, message: MESSAGE, bench: BENCH }) {
  const z = await api.get(`/api/export/download?workflows=${flow.platform}&target=${engine}&withSimulators=true&tenant=${TENANT}`);
  expect(z.status).toBe(200);
  const file = path.join(work, `${engine}-${label}.zip`);
  fs.writeFileSync(file, z.data);
  const bench = [...flow.bench.filter((b) => !extra.some((o) => o.port === b.port)), ...extra];
  const args = ['src/cli.ts', 'deploy', file, '--workflow', flow.platform, '--message', flow.message, '--inbound', path.join(STUDIO, 'fixtures', sample), '--tenant', TENANT, ...bench.flatMap((b) => ['--bench', `${b.port}=${b.simulator}:${b.scenario}`])];
  // Asynchronous, so the studio server's kept-alive connections stay open while the engine runs.
  const out = await new Promise<string>((resolve) => {
    let text = '';
    const child = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), args, { cwd: STUDIO, env: process.env });
    const timer = setTimeout(() => child.kill('SIGKILL'), RUN_MS);
    child.stdout.on('data', (d) => (text += d));
    child.stderr.on('data', (d) => (text += d));
    child.on('close', () => { clearTimeout(timer); resolve(text); });
  });
  const workers = [...out.matchAll(/worker (th-[\w-]+) \(pid (\d+)\) served (\{.*\}), refused (\d+)/g)].map((m) => ({ queue: m[1], pid: m[2], served: JSON.parse(m[3]), refused: Number(m[4]) }));
  return { out, workers, trace: /^trace  (.*)$/m.exec(out)?.[1].split(' | ') ?? [], outcome: /^outcome (\w+)/m.exec(out)?.[1] };
}

const NAMES: Record<string, string> = { dtfx: 'DTFx', elsa: 'Elsa' };
// Plan P2: the WPS payment file with the bank as the agent, in the source's layout: confirmed, credited (one credit refused),
// reported, the refused credit returned in a returns file and the return recorded, each way the studio's interpreter gives it.
const INWARD = {
  platform: 'p.wf.inward.v2', message: 'uae.wps.pif', sample: 'samples/wps-pif-ssm.pif',
  bench: [
    { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-inward.v1', scenario: 'payment-file-returns-accepted' },
    { port: 'p.port.core-banking.v1', simulator: 'p.sim.core-banking.v1', scenario: 'reject-first-then-accept' },
    ...['sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
    { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'reject' },
  ],
  // A simulator's scenario state is its worker's, one per queue: the return's posting (queue th-uae-wps) would be the first call
  // its core-banking simulator sees, not the third (Issue I-84). The return recorded through core banking is on the runtime, one
  // core banking for every queue, in e2e-wps-inward-native.
  cases: [
    ['every credit posted: confirmed, credited and reported', [{ port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-inward.v1', scenario: 'payment-file' }, { port: 'p.port.core-banking.v1', simulator: 'p.sim.core-banking.v1', scenario: 'accept' }]],
    ['a returns file WPS refuses, handed to operations', [{ port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-inward.v1', scenario: 'payment-file-returns-refused' }]],
  ] as [string, any[]][],
};

for (const engine of ENGINES) {
  describe(`the WPS parity suite on ${NAMES[engine]} (${engine})`, () => {
    it(`${engine}: settles through the front door, the corporate workflow and the WPS leg, on four queues and four workers`, async () => {
      const d = await deploy(engine, 'settle');
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
      expect(d.workers.find((w) => w.queue === 'th-uae-wps')!.served['uae.wps.exchange.send-salary-file.v2@2.0.0']).toBe(1);
      if (ON_STORE) expect(/^store: .*$/m.exec(d.out)?.[0] ?? '', d.out.slice(-2500)).toMatch(/chains unbroken, all match the run$/);
    }, RUN_MS);

    // The same salary file and the same party behaviour give the same trace as on the studio's interpreter.
    const cases: [string, any[]][] = [
      ['the scheme rejects the file', [{ port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'reject' }]],
      ['sanctions screening hits', [{ port: 'p.port.sanctions.v1', simulator: 'p.sim.sanctions.v1', scenario: 'reject' }]],
      ['a file of two lines, with operations declining any hold', [OPS('decline')]],
      ['a file of two lines, with operations approving any hold', [OPS('approve')]],
    ];
    for (const [what, override] of INWARD.cases) {
      it(`${engine}: the inward payment file, ${what}: the same trace as the studio interpreter`, async () => {
        const bench = [...INWARD.bench.filter((b) => !override.some((o) => o.port === b.port)), ...override];
        const expected = await api.post('/api/bench/run', { target: INWARD.platform, tenant: TENANT, inbound: INWARD.sample, message: INWARD.message, bench });
        expect(expected.status, JSON.stringify(expected.data).slice(0, 400)).toBe(200);
        expect(expected.data.trace).toContain('disposition:accept');
        const d = await deploy(engine, `inward-${what.replace(/\W+/g, '-')}`, INWARD.sample, override, INWARD);
        expect(d.trace, d.out.slice(-2500)).toEqual(expected.data.trace);
        expect(d.outcome).toBe(expected.data.outcome);
        expect(d.out).toContain('front door: uae.wps.wf.agent-payment-in.v2 fills scheme-leg');
        // The leg ran past the supplementary file to the disbursement file: the PSI reached it (R-05-30).
        expect(d.out).toContain('"uae.wps.map.dif-out.v2@2.0.0":1');
      }, RUN_MS);
    }
    for (const [what, override] of cases) {
      it(`${engine}: ${what}: the same trace as the studio interpreter`, async () => {
        const bench = [...BENCH.filter((b) => !override.some((o) => o.port === b.port)), ...override];
        const sample = what.includes('two lines') ? TWO_LINES : SAMPLE;
        const expected = await api.post('/api/bench/run', { target: PLATFORM, tenant: TENANT, inbound: sample, message: MESSAGE, bench });
        expect(expected.status, JSON.stringify(expected.data).slice(0, 400)).toBe(200);
        const d = await deploy(engine, what.replace(/\W+/g, '-'), sample, override);
        expect(d.trace, d.out.slice(-2500)).toEqual(expected.data.trace);
        expect(d.outcome).toBe(expected.data.outcome);
      }, RUN_MS);
    }
  });
}
