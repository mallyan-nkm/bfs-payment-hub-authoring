import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { parseYaml } from '../../src/core/yaml.js';
import { unzip } from '../../src/export/zip.js';
import { client, startServer, STUDIO, type Server } from './server.js';

// End to end, black box: a designer builds a WPS workflow from scratch through the studio's HTTP
// API, publishes it through maker-checker, exports it for DTFx with separate workers, deploys it and
// runs it. Nothing here reaches into the studio's code: only HTTP, the export zip and the CLI.

const MAKER = 'shared.designer';
const CHECKER = 'shared.checker';
const PROV = { origin: 'factory', status: 'provisional', clauses: ['Public SIF description, SCR record'], note: 'Built end to end in the test suite.' };
const HEADER = { paymentId: 'P-1', endToEndId: 'E2E-1', kind: 'payment', direction: 'outward', segment: 'corporate', scheme: 'uae.wps' };

const RULE = {
  kind: 'rule', name: 'uae.wps.rule.salary-month-valid.v1', release: '1.0.0', title: 'Salary month is a valid month', ruleKind: 'field', level: 'batch', repeatability: 'non-repeatable',
  when: [{ match: { fact: 'WpsBatch', as: 'w', where: "not matches(w.salaryMonth, '^(0[1-9]|1[0-2])[0-9]{4}$')" } }],
  then: [{ yield: { finding: { severity: 'reject', path: 'ext.uae.wps.salaryMonth', reasonCode: 'DT01' } } }],
  examples: [
    { name: 'passes', evaluationDate: '2026-11-17', given: { WpsBatch: { salaryMonth: '102026' } }, expect: { findings: [] } },
    { name: 'boundary, December', evaluationDate: '2026-11-17', given: { WpsBatch: { salaryMonth: '122026' } }, expect: { findings: [] } },
    { name: 'fails, month 13', evaluationDate: '2026-11-17', given: { WpsBatch: { salaryMonth: '132026' } }, expect: { findings: [{ reasonCode: 'DT01' }] } },
  ],
  provenance: PROV,
};
const RULESET = { kind: 'ruleset', name: 'uae.wps.ruleset.salary-month.v1', release: '1.0.0', title: 'Salary month checks', mode: 'validation', stages: ['field'], rules: [RULE.name], provenance: PROV };
const ACTIVITY = {
  kind: 'activity', name: 'uae.wps.validate.salary-month.v1', release: '1.0.0', title: 'Salary month check', type: 'validate', level: 'batch',
  reads: ['ext.uae.wps'], writes: ['facts.validation'], outcomes: [{ name: 'pass', primary: true }, { name: 'reject' }], effect: 'pure',
  ruleSets: [RULESET.name], implementation: { binding: 'builtin', service: 'rule-service' }, provenance: PROV,
};
const ACTIVITY_TESTS = [
  { kind: 'test', id: 'WPS-MON-001', title: 'A valid salary month passes', target: ACTIVITY.name, level: 'activity', evaluationDate: '2026-11-17', given: { context: { header: HEADER, ext: { uae: { wps: { salaryMonth: '102026' } } } } }, expect: { outcome: 'pass', findings: [] } },
  { kind: 'test', id: 'WPS-MON-002', title: 'Month 13 is rejected', target: ACTIVITY.name, level: 'activity', evaluationDate: '2026-11-17', given: { context: { header: HEADER, ext: { uae: { wps: { salaryMonth: '132026' } } } } }, expect: { outcome: 'reject', findings: [{ reasonCode: 'DT01' }] } },
];
const LEG = {
  kind: 'workflow', wfKind: 'leg', name: 'uae.wps.wf.salary-file-checked.v1', release: '1.0.0', dsl: '1.0.0', title: 'WPS salary file with a salary month check',
  description: 'Built from scratch in the end-to-end test: the salary file leg with a salary month check before the records.',
  direction: 'outward', implements: { slot: 'scheme-leg', of: ['p.wf.corporate.v1'] },
  entry: { reads: ['header', 'batch', 'items[]', 'ext.uae.wps', 'facts.reservation'] },
  exit: { writes: ['status', 'messages'], outcomes: ['settled', 'rejected', 'pending'] },
  do: [
    { id: 'business-date', call: 'uae.p.enrich.business-date.v1' },
    { id: 'file-checks', call: 'uae.wps.validate.salary-file.v1', handle: { repair: { end: 'rejected' }, reject: { end: 'rejected' } } },
    { id: 'month-check', call: ACTIVITY.name, handle: { reject: { end: 'rejected' } } },
    { forEach: { in: 'items[]', onItemFailure: 'stop', do: [
      { id: 'record-checks', call: 'uae.wps.validate.salary-record.v1', level: 'item', handle: { reject: { end: 'rejected' } } },
      { id: 'record-iban', call: 'uae.p.validate.iban.v1', level: 'item', handle: { reject: { end: 'rejected' } } },
    ] } },
    { id: 'render', call: 'uae.wps.map.sif-out.v1', handle: { unmappable: { end: 'rejected' } } },
    { id: 'submit', call: 'uae.wps.exchange.submit-salary-file.v1', handle: { rejected: { end: 'rejected' }, pending: { end: 'pending' } } },
    { end: 'settled' },
  ],
  provenance: PROV,
};
const BENCH = (scenario: string) => [{ port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario }, { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' }];
const LEG_TESTS = [
  { kind: 'test', id: 'WPS-CHK-001', title: 'The checked salary file is acknowledged', target: LEG.name, level: 'leg', evaluationDate: '2026-11-17', bench: BENCH('accept'), given: { inbound: 'samples/wps-sif-valid.sif' },
    expect: { outcome: 'settled', trace: ['business-date:set', 'file-checks:pass', 'month-check:pass', 'record-checks:pass', 'record-iban:pass', 'render:mapped', 'submit:accepted', 'end:settled'], detail: ['submit.send:accepted', 'submit.await-ack:received', 'submit.read-ack:ack'] } },
  { kind: 'test', id: 'WPS-CHK-002', title: 'Refused by WPS', target: LEG.name, level: 'leg', evaluationDate: '2026-11-17', bench: BENCH('reject'), given: { inbound: 'samples/wps-sif-valid.sif' }, expect: { outcome: 'rejected', reasonCode: 'AM10' } },
  { kind: 'test', id: 'WPS-CHK-003', title: 'A salary month of 13 is rejected before anything is sent', target: LEG.name, level: 'leg', evaluationDate: '2026-11-17', bench: BENCH('accept'), given: { inbound: 'samples/wps-sif-bad-month.sif' },
    expect: { outcome: 'rejected', reasonCode: 'DT01', trace: ['business-date:set', 'file-checks:pass', 'month-check:reject', 'end:rejected'], messages: [{ port: 'uae.wps.port.wps.v1', message: 'uae.wps.sif-submission', count: 0 }] } },
  { kind: 'test', id: 'WPS-CHK-004', title: 'No acknowledgement within the reply time is pending', target: LEG.name, level: 'leg', evaluationDate: '2026-11-17', bench: BENCH('no-reply'), given: { inbound: 'samples/wps-sif-valid.sif' }, expect: { outcome: 'pending', virtualElapsed: 'P1D' } },
];

let server: Server;
let api: ReturnType<typeof client>;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-'));

beforeAll(async () => {
  server = await startServer();
  api = client(server.url);
}, 60000);
afterAll(() => server?.stop());

/** Save as a draft, pass the gates, submit for review and publish with a different checker. */
async function publish(doc: any, tests: any[] = []) {
  const saved = await api.post('/api/drafts', { tenant: 'owner', actor: MAKER, doc });
  expect(saved.status, JSON.stringify(saved.data)).toBe(200);
  const errors = saved.data.issues.filter((i: any) => i.severity === 'error');
  expect(errors, JSON.stringify(errors)).toEqual([]);
  const gates = await api.post('/api/gates', { doc, tests });
  expect(gates.data.passed, JSON.stringify(gates.data.firstRed)).toBe(true);
  const key = saved.data.draft.key;
  expect((await api.post('/api/review', { tenant: 'owner', actor: MAKER, key })).status).toBe(200);
  const pub = await api.post('/api/publish', { tenant: 'owner', key, checker: CHECKER });
  expect(pub.status, JSON.stringify(pub.data)).toBe(200);
  return pub.data.published;
}

function cli(args: string[]) {
  const r = spawnSync(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), ['src/cli.ts', ...args], { cwd: STUDIO, encoding: 'utf8', env: { ...process.env, TH_DATA_ROOT: '' }, timeout: 300000 });
  return { code: r.status, out: (r.stdout ?? '') + (r.stderr ?? '') };
}

describe('a WPS workflow built from scratch, end to end', () => {
  it('starts from a blank leg', async () => {
    const r = await api.post('/api/new', { kind: 'workflow', wfKind: 'leg', name: LEG.name, title: LEG.title, from: 'blank' });
    expect(r.data.doc).toMatchObject({ kind: 'workflow', wfKind: 'leg', name: LEG.name });
    const check = await api.post('/api/check', { doc: { ...LEG, do: [{ id: 'month-check', call: ACTIVITY.name }] } });
    expect(check.data.issues.some((i: any) => i.code === 'L03' && i.step === 'month-check')).toBe(true); // the block does not exist yet
  });

  it('refuses a bank designer authoring shared content, and a maker approving their own publish', async () => {
    const bank = await api.post('/api/drafts', { tenant: 'example-bank', actor: 'example.maker', doc: RULE });
    expect(bank.status).toBe(409);
    const saved = await api.post('/api/drafts', { tenant: 'owner', actor: MAKER, doc: { ...RULE, release: '1.0.0', title: 'Scratch copy' } });
    const self = await api.post('/api/publish', { tenant: 'owner', key: saved.data.draft.key, checker: MAKER });
    expect(self.status).toBe(409);
    expect((await api.del(`/api/drafts/${encodeURIComponent(saved.data.draft.key)}?tenant=owner&actor=${MAKER}`)).status).toBe(200);
  });

  it('authors and publishes the rule, the rule set and the activity, with tests per outcome', async () => {
    await publish(RULE);
    await publish(RULESET);
    await publish(ACTIVITY, ACTIVITY_TESTS);
    for (const t of ACTIVITY_TESTS) await publish(t);
    for (const t of ACTIVITY_TESTS) {
      const r = await api.post(`/api/tests/${t.id}/run`);
      expect(r.data.passed, JSON.stringify(r.data.checks)).toBe(true);
    }
  });

  it('composes the leg, passes its gates with four tests, and publishes it', async () => {
    const check = await api.post('/api/check', { doc: LEG });
    expect(check.data.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
    const x = await api.post('/api/expanded', { doc: LEG });
    expect(x.data.doc.form).toBe('expanded');
    expect(JSON.stringify(x.data.doc)).toContain('submit.await-ack');
    await publish(LEG, LEG_TESTS);
    for (const t of LEG_TESTS) await publish(t);
    const cov = await api.get(`/api/test-coverage/${LEG.name}`);
    expect(cov.data.untested).toEqual([]);
    for (const t of LEG_TESTS) {
      const r = await api.post(`/api/tests/${t.id}/run`);
      expect(r.data.passed, `${t.id} ${JSON.stringify(r.data.checks.filter((c: any) => !c.passed))}`).toBe(true);
    }
    const cat = await api.get('/api/catalogue?q=salary-file-checked');
    expect(cat.data.items.map((i: any) => i.name)).toContain(LEG.name);
    const audit = await api.get('/api/audit?tenant=owner');
    expect(audit.data.filter((a: any) => a.action === 'publish' && a.outcome === 'done').length).toBeGreaterThanOrEqual(9);
    expect((await api.get('/api/audit/verify')).data.ok).toBe(true);
  });

  let zip = '';
  it('exports the leg for DTFx with a separate worker per task queue', async () => {
    const r = await api.get(`/api/export/download?workflows=${LEG.name}&target=dtfx&withSimulators=true`);
    expect(r.status).toBe(200);
    zip = path.join(work, 'wps-checked.zip');
    fs.writeFileSync(zip, r.data);
    const files = new Map(unzip(fs.readFileSync(zip)).map((e) => [e.name, e.content.toString('utf8')]));
    const manifest = parseYaml(files.get('manifest.yaml')!);
    expect(manifest.workers.map((w: any) => w.queue)).toEqual(['th-uae-p', 'th-uae-wps']);
    const wps = manifest.workers.find((w: any) => w.queue === 'th-uae-wps');
    expect(wps.tasks).toContain('uae.wps.validate.salary-month.v1@1.0.0');
    expect(manifest.workers.find((w: any) => w.queue === 'th-uae-p').tasks.every((t: string) => t.startsWith('uae.p.'))).toBe(true);
    expect(manifest.promotable).toBe(false); // it carries simulators
    const wf = parseYaml(files.get(`workflows/${LEG.name}@1.0.0.yaml`)!);
    expect(wf.form).toBe('expanded');
    expect(wf.artefacts.map((a: any) => a.name)).toEqual(expect.arrayContaining([ACTIVITY.name, RULESET.name, RULE.name, 'uae.wps.sub.submit-salary-file.v1']));
    expect(files.get('workers/th-uae-wps/artefacts/rule/uae.wps.rule.salary-month-valid.v1@1.0.0.yaml')).toBeDefined();
    expect(files.has('workers/th-uae-p/artefacts/rule/uae.wps.rule.salary-month-valid.v1@1.0.0.yaml')).toBe(false);
  });

  const deploy = (scenario: string, sample: string) => cli(['deploy', zip, '--workflow', LEG.name, '--inbound', path.join(STUDIO, 'fixtures', 'samples', sample), '--bench', `uae.wps.port.wps.v1=uae.wps.sim.wps-outward.v1:${scenario}`, '--bench', 'uae.p.port.calendar.v1=uae.p.sim.calendar.v1:accept']);

  it('deploys and runs on DTFx: an acknowledged file settles, each worker serving only its queue', () => {
    const r = deploy('accept', 'wps-sif-valid.sif');
    expect(r.out).toContain('outcome settled');
    expect(r.out).toContain('trace  business-date:set | file-checks:pass | month-check:pass | record-checks[0]:pass | record-iban[0]:pass | render:mapped | submit:accepted | end:settled');
    expect(r.out).toContain('detail submit.send:accepted | submit.await-ack:received | submit.read-ack:ack');
    const pids = [...r.out.matchAll(/worker (th-[\w-]+) \(pid (\d+)\) served (\{.*\}), refused (\d+)/g)].map((m) => ({ queue: m[1], pid: m[2], served: JSON.parse(m[3]), refused: Number(m[4]) }));
    expect(pids.length).toBe(2);
    expect(new Set(pids.map((p) => p.pid)).size).toBe(2); // two separate processes
    for (const p of pids) {
      expect(p.refused).toBe(0);
      const scope = p.queue === 'th-uae-p' ? 'uae.p.' : 'uae.wps.';
      expect(Object.keys(p.served).every((t) => t.startsWith(scope))).toBe(true);
    }
    expect(pids.find((p) => p.queue === 'th-uae-wps')!.served['uae.wps.validate.salary-month.v1@1.0.0']).toBe(1);
  }, 300000);

  it('deploys and runs on DTFx: a refused file is rejected', () => {
    const r = deploy('reject', 'wps-sif-valid.sif');
    expect(r.out).toContain('outcome rejected');
    expect(r.out).toContain('submit:rejected');
  }, 300000);

  it('deploys and runs on DTFx: a salary month of 13 stops at the new check and sends nothing', () => {
    const r = deploy('accept', 'wps-sif-bad-month.sif');
    expect(r.out).toContain('trace  business-date:set | file-checks:pass | month-check:reject | end:rejected');
    expect(r.out).not.toContain('uae.wps.exchange.send-salary-file');
  }, 300000);
});
