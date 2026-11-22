import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { parseYaml, toYaml } from '../../src/core/yaml.js';
import { unzip } from '../../src/export/zip.js';
import { client, startServer, STUDIO, type Server } from './server.js';

// End to end, black box: WPS is a factory scheme, so a bank does not author WPS content. It changes
// only its own configuration set, and the factory salary file flow behaves accordingly: through the
// front door, the corporate payment workflow and the WPS leg in its slot. Each configuration is
// exported for the bank and deployed on DTFx with a separate worker per task queue.

const TENANT = 'example-bank';
const MAKER = 'example.maker';
const SAMPLE = 'samples/wps-sif-valid.sif';
const MESSAGE = 'uae.wps.sif';
const PLATFORM = 'p.wf.corporate.v1';
const BENCH = [
  ...['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
];
const SETTLED = ['validate:pass', 'duplicate:unique', 'hook:done', 'sanctions:clear', 'fraud:clear', 'join:all', 'limits:pass', 'reserve:reserved', 'route:decided', 'hook:continue', 'leg:settled', 'post:posted', 'verify:verified', 'finally:notify', 'finally:publish', 'hook:done', 'end:settled'];
const NO_WPS_RAIL = ['validate:pass', 'duplicate:unique', 'hook:done', 'sanctions:clear', 'fraud:clear', 'join:all', 'limits:pass', 'reserve:reserved', 'route:no-match', 'undo:reserve', 'finally:notify', 'finally:publish', 'hook:done', 'end:rejected'];
const NO_HOLD_HOOK = SETTLED.filter((s, i) => !(s === 'hook:continue' && i === 9));
const HELD = (decision: 'approved' | 'declined') => [...SETTLED.slice(0, 9), 'hook:hold', `hold:${decision}`, ...(decision === 'approved' ? SETTLED.slice(10) : ['undo:reserve', 'finally:notify', 'finally:publish', 'hook:done', 'end:rejected'])];
const TWO_LINES = 'samples/wps-sif-two-lines.sif'; // lines of 1000 and 500, total 1500
const OPS = (scenario: string) => ({ port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario });

let server: Server;
let api: ReturnType<typeof client>;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-wps-'));
let shipped: any;

beforeAll(async () => {
  server = await startServer();
  api = client(server.url);
  shipped = (await api.get(`/api/tenants/${TENANT}/configuration`)).data.configurationSet;
}, 60000);
afterAll(() => server?.stop());

/** A new release of the bank's configuration set, checked, validated, saved by the bank configurator and approved as a version by the bank approver (R-11-31). */
async function release(version: string, change: (c: any) => void) {
  const c = JSON.parse(JSON.stringify(shipped));
  c.release = version;
  change(c);
  const check = await api.post(`/api/tenants/${TENANT}/configuration/validate`, { doc: c });
  expect(check.data.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
  const saved = await api.post(`/api/tenants/${TENANT}/configuration`, { actor: MAKER, yaml: toYaml(c) });
  expect(saved.status, JSON.stringify(saved.data)).toBe(200);
  const approved = await api.post('/api/publish', { tenant: TENANT, key: saved.data.draft.key, checker: 'example.checker' });
  expect(approved.status, JSON.stringify(approved.data).slice(0, 400)).toBe(200);
  expect((await api.get(`/api/tenants/${TENANT}/configuration`)).data.configurationSet.release).toBe(version);
  return c;
}

/** The salary file end to end on the studio interpreter, for the bank. */
async function bench(sample = SAMPLE, extra: any[] = []) {
  const r = await api.post('/api/bench/run', { target: PLATFORM, tenant: TENANT, inbound: sample, message: MESSAGE, bench: [...BENCH, ...extra] });
  expect(r.status, JSON.stringify(r.data).slice(0, 400)).toBe(200);
  return r.data;
}

/** Export the corporate workflow for the bank and deploy the salary file on DTFx. */
function deploy(label: string, sample = SAMPLE, extra: any[] = []) {
  return (async () => {
    const z = await api.get(`/api/export/download?workflows=${PLATFORM}&target=dtfx&withSimulators=true&tenant=${TENANT}`);
    expect(z.status).toBe(200);
    const file = path.join(work, `${label}.zip`);
    fs.writeFileSync(file, z.data);
    const args = ['src/cli.ts', 'deploy', file, '--workflow', PLATFORM, '--message', MESSAGE, '--inbound', path.join(STUDIO, 'fixtures', sample), '--tenant', TENANT, ...[...BENCH, ...extra].flatMap((b) => ['--bench', `${b.port}=${b.simulator}:${b.scenario}`])];
    const r = spawnSync(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), args, { cwd: STUDIO, encoding: 'utf8', env: { ...process.env }, timeout: 300000 });
    const out = (r.stdout ?? '') + (r.stderr ?? '');
    const workers = [...out.matchAll(/worker (th-[\w-]+) \(pid (\d+)\) served (\{.*\}), refused (\d+)/g)].map((m) => ({ queue: m[1], pid: m[2], served: JSON.parse(m[3]), refused: Number(m[4]) }));
    return { out, file, workers, trace: /^trace  (.*)$/m.exec(out)?.[1].split(' | ') ?? [], outcome: /^outcome (\w+)/m.exec(out)?.[1] };
  })();
}

describe('the factory WPS scheme, changed only by bank configuration', () => {
  it('starts from the shipped configuration: WPS is enabled and the bank holds no WPS content of its own', async () => {
    expect(shipped.release).toBe('1.0.0');
    expect(shipped.values.find((v: any) => v.parameter === 'p.table.rail-choice.v1#enabledRails').value).toContain('uae.wps');
    const scheme = (await api.get('/api/schemes/uae.wps')).data.scheme;
    expect(scheme.messages.find((m: any) => m.id === MESSAGE).disposition.start.leg).toBe('uae.wps.wf.salary-file-out.v1');
    // A bank cannot author in a shared scope, so configuration is its only lever.
    const refused = await api.post('/api/drafts', { tenant: TENANT, actor: MAKER, doc: { kind: 'rule', name: 'uae.wps.rule.bank-copy.v1', release: '1.0.0', title: 'x' } });
    expect(refused.status).toBe(403);
  });

  it('as shipped, a salary file settles: front door, corporate workflow, WPS leg, bank hooks', async () => {
    const r = await bench();
    expect(r.outcome).toBe('settled');
    expect(r.trace).toEqual(SETTLED);
  });

  it('refuses configuration a bank may not set: a limit above its bound, the same release again, a checker as author', async () => {
    const over = JSON.parse(JSON.stringify(shipped));
    over.release = '1.0.1';
    over.values.find((v: any) => v.parameter === 'b.rule.internal-limit.v1#internalLimit').value = 2000000;
    expect((await api.post(`/api/tenants/${TENANT}/configuration/validate`, { doc: over })).data.issues.some((i: any) => i.severity === 'error')).toBe(true);
    expect((await api.post(`/api/tenants/${TENANT}/configuration`, { actor: MAKER, yaml: toYaml(shipped) })).status).toBe(409);
    expect((await api.post(`/api/tenants/${TENANT}/configuration`, { actor: 'example.checker', yaml: toYaml({ ...shipped, release: '1.0.2' }) })).status).toBe(403);
  });

  it('1.1.0 turns the WPS rail off: the payment is rejected at the route and its reservation undone', async () => {
    await release('1.1.0', (c) => { c.values.find((v: any) => v.parameter === 'p.table.rail-choice.v1#enabledRails').value = []; });
    const r = await bench();
    expect(r.outcome).toBe('rejected');
    expect(r.trace).toEqual(NO_WPS_RAIL);
  });

  it('1.1.0 deployed on DTFx: rejected the same way, and nothing reaches the WPS worker past the intake', async () => {
    const d = await deploy('1.1.0');
    expect(d.out).toContain(`configuration ${TENANT}@1.1.0 from the package`);
    expect(d.outcome, d.out.slice(-1500)).toBe('rejected');
    expect(d.trace).toEqual(NO_WPS_RAIL);
    const wps = d.workers.find((w) => w.queue === 'th-uae-wps')!;
    expect(Object.keys(wps.served)).toEqual(['uae.wps.map.sif-in.v1@1.0.0']);
  }, 300000);

  it('1.2.0 turns WPS back on and unbinds the pre-release hook: the payment settles without it', async () => {
    await release('1.2.0', (c) => { c.bindings = c.bindings.filter((b: any) => b.hookPoint !== 'p.wf.corporate.v1#before-release'); });
    const r = await bench();
    expect(r.outcome).toBe('settled');
    expect(r.trace).toEqual(NO_HOLD_HOOK);
  });

  it('1.2.0 deployed on DTFx: settled through four separate workers, each serving only its queue', async () => {
    const d = await deploy('1.2.0');
    const files = new Map(unzip(fs.readFileSync(d.file)).map((e) => [e.name, e.content.toString('utf8')]));
    expect(parseYaml(files.get(`configuration/${TENANT}.yaml`)!).release).toBe('1.2.0');
    expect(d.out).toContain('front door: workflow-select chose p.wf.corporate.v1 for payment outward corporate');
    expect(d.out).toContain('front door: uae.wps.wf.salary-file-out.v1 fills scheme-leg');
    expect(d.outcome, d.out.slice(-1500)).toBe('settled');
    expect(d.trace).toEqual(NO_HOLD_HOOK);
    expect(d.workers.map((w) => w.queue).sort()).toEqual(['th-bank', 'th-p', 'th-uae-p', 'th-uae-wps']);
    expect(new Set(d.workers.map((w) => w.pid)).size).toBe(4);
    const scope: Record<string, string> = { 'th-bank': 'b.', 'th-p': 'p.', 'th-uae-p': 'uae.p.', 'th-uae-wps': 'uae.wps.' };
    for (const w of d.workers) {
      expect(w.refused).toBe(0);
      expect(Object.keys(w.served).every((t) => t.startsWith(scope[w.queue])), `${w.queue} ${Object.keys(w.served)}`).toBe(true);
    }
    // The unbound hook's activity never ran; the cost-centre and close hooks still did.
    const bank = d.workers.find((w) => w.queue === 'th-bank')!;
    expect(bank.served['b.validate.internal-limit.v1@1.0.0']).toBeUndefined();
    expect(bank.served['b.enrich.cost-centre.v1@1.0.0']).toBe(1);
  }, 300000);

  // The bank's limit holds each line of a file and the file total (Issue I-28, decided). A held
  // payment waits for the bank's operations: approved, it goes on; declined, it is rejected.
  const limit = (value: number) => (c: any) => {
    c.bindings = shipped.bindings;
    c.values.find((v: any) => v.parameter === 'b.rule.internal-limit.v1#internalLimit').value = value;
  };

  it('1.3.0 sets the internal limit to 1200: every line is within, the file total is not, so the payment is held', async () => {
    await release('1.3.0', limit(1200));
    const approved = await bench(TWO_LINES, [OPS('approve')]);
    expect(approved.trace).toEqual(HELD('approved'));
    expect(approved.outcome).toBe('settled');
    const declined = await bench(TWO_LINES, [OPS('decline')]);
    expect(declined.trace).toEqual(HELD('declined'));
    expect(declined.outcome).toBe('rejected');
  });

  it('1.3.0 deployed on DTFx: held on the file total and declined, the bank worker ran the limit', async () => {
    const d = await deploy('1.3.0', TWO_LINES, [OPS('decline')]);
    expect(d.outcome, d.out.slice(-1500)).toBe('rejected');
    expect(d.trace).toEqual(HELD('declined'));
    expect(d.workers.find((w) => w.queue === 'th-bank')!.served['b.validate.internal-limit.v1@1.0.0']).toBe(1);
    expect(d.workers.find((w) => w.queue === 'th-p')!.served['p.hold.approval.v1@1.0.0']).toBe(1);
  }, 300000);

  it('1.4.0 sets the limit to 900: the line of 1000 is above it, so the payment is held', async () => {
    await release('1.4.0', limit(900));
    expect((await bench(TWO_LINES, [OPS('approve')])).trace).toEqual(HELD('approved'));
  });

  it('1.5.0 sets the limit to 2000: every line and the total are within, so nothing is held', async () => {
    await release('1.5.0', limit(2000));
    const r = await bench(TWO_LINES, [OPS('decline')]);
    expect(r.trace).toEqual(SETTLED);
  });

  it('changed nothing in the factory: no WPS artefact was drafted or published, and the audit chain verifies', async () => {
    for (const t of ['owner', TENANT]) {
      const audit = (await api.get(`/api/audit?tenant=${t}`)).data;
      expect(audit.filter((a: any) => a.outcome === 'done' && /^uae\.wps\./.test(a.key ?? ''))).toEqual([]);
    }
    const bank = (await api.get(`/api/audit?tenant=${TENANT}`)).data;
    // Each configuration release is a version a bank approver approved (R-11-31).
    const versions = bank.filter((a: any) => a.action === 'publish' && a.outcome !== 'refused' && /config:example-bank@/.test(a.key ?? ''));
    expect(versions.map((a: any) => /@(\S+)$/.exec(a.key)![1]).sort()).toEqual(['1.1.0', '1.2.0', '1.3.0', '1.4.0', '1.5.0']);
    expect(versions.every((a: any) => a.actor === 'example.checker' && /maker example\.maker/.test(a.detail))).toBe(true);
    expect(bank.some((a: any) => a.outcome === 'refused')).toBe(true);
    expect((await api.get('/api/audit/verify')).data.ok).toBe(true);
  });
});
