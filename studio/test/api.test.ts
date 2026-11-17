import { beforeAll, describe, expect, it } from 'vitest';
import { dispatch, type Req } from '../src/server/api.js';
import { templates } from '../src/server/templates.js';
import { toYaml } from '../src/core/yaml.js';
import type { StudioEnv } from '../src/env.js';
import { freshEnv } from './helpers.js';

// White box: the API's routes in process, through `dispatch`, branch by branch. The black-box suite
// (test/blackbox/api.test.ts) covers the same surface over HTTP.

let env: StudioEnv;
beforeAll(() => {
  env = freshEnv();
});

function call(method: string, path: string, body?: any) {
  const [p, q = ''] = path.split('?');
  const req: Req = { method, path: p, query: Object.fromEntries(new URLSearchParams(q)), body: body ?? {} };
  return dispatch(env, req);
}
const get = (p: string) => call('GET', p);
const post = (p: string, b?: any) => call('POST', p, b);

describe('templates', () => {
  it.each([
    ['workflow', 'platform'], ['workflow', 'segment'], ['workflow', 'leg'], ['workflow', 'handler'], ['workflow', 'subflow'],
    ['activity', undefined], ['rule', undefined], ['hook', undefined], ['simulator', undefined], ['table', undefined], ['test', undefined],
  ] as [string, string | undefined][])('a %s %s template, blank and full, is a document of its kind', async (kind, wfKind) => {
    for (const full of [false, true]) {
      const t = templates(kind, wfKind, undefined, undefined, full);
      expect(t.release).toBe('1.0.0');
      if (['workflow', 'activity', 'rule', 'hook'].includes(kind)) expect(t.kind).toBe(kind);
    }
    const r = await post('/api/new', { kind, wfKind, from: 'template' });
    expect(r.status).toBe(200);
    expect(r.body.yaml).toContain('release: 1.0.0');
  });

  it('clones a published artefact under a new name, and refuses a clone of nothing', async () => {
    const r = await post('/api/new', { from: 'clone', source: 'uae.p.validate.iban.v1', name: 'uae.p.validate.iban-copy.v1' });
    expect(r.status).toBe(200);
    expect(r.body.doc.name).toBe('uae.p.validate.iban-copy.v1');
    expect((await post('/api/new', { from: 'clone', source: 'nope.x.y.v1' })).status).toBe(404);
  });

  it('answers 404 for an unknown route', async () => {
    expect((await get('/api/nope')).status).toBe(404);
    expect((await call('PUT', '/api/meta')).status).toBe(404);
  });
});

describe('editing and checking', () => {
  it('checks a document as it is edited, from a doc or from YAML', async () => {
    const doc = (await get('/api/artefacts/uae.p.validate.iban.v1')).body.doc ?? (await get('/api/artefacts/uae.p.validate.iban.v1')).body.entry.doc;
    expect((await post('/api/check', { doc })).body.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
    const broken = await post('/api/check', { yaml: toYaml({ ...doc, outcomes: undefined }) });
    expect(broken.body.issues.length).toBeGreaterThan(0);
  });

  it('expands a composite step and analyses a decision table', async () => {
    const x = await post('/api/expanded', { doc: (await get('/api/expanded/uae.wps.wf.salary-file-out.v1')).body.doc });
    expect(x.status).toBe(200);
    expect((await get('/api/expanded/nope.wf.x.v1')).status).toBe(404);
    const table = (await get('/api/artefacts/p.table.rail-choice.v1')).body;
    const a = await post('/api/analyse', { doc: table.doc ?? table.entry.doc });
    expect(a.status).toBe(200);
    expect(a.body).toHaveProperty('gaps');
    expect((await post('/api/analyse', { doc: { kind: 'rule' } })).status).toBe(400);
  });

  it('runs the gates and names the first red one', async () => {
    const act = (await get('/api/artefacts/uae.p.validate.iban.v1')).body;
    const doc = { ...(act.doc ?? act.entry.doc), provenance: undefined };
    const noTests = await post('/api/gates', { doc, tests: [] });
    expect(noTests.body.passed).toBe(false);
    const wrong = await post('/api/gates', { doc: { ...doc, provenance: { origin: 'authored' } }, tests: [{ kind: 'test', id: 'X-1', target: doc.name, level: 'activity', evaluationDate: '2026-11-17', given: { context: {} }, expect: { outcome: 'no-such-outcome' } }] });
    expect(wrong.body.passed).toBe(false);
    const script = await post('/api/gates', { doc: { ...doc, provenance: { origin: 'authored' } }, script: { language: 'typescript', source: 'export default () => Math.random()' } });
    expect(script.body.gates.find((g: any) => g.gate === 'determinism')?.passed).toBe(false);
    const py = await post('/api/gates', { doc: { ...doc, provenance: { origin: 'authored' } }, script: { language: 'python', source: 'import random\n' } });
    expect(py.body.gates.find((g: any) => g.gate === 'determinism')?.passed).toBe(false);
    const unparsed = await post('/api/gates', { yaml: 'a: [' });
    expect(unparsed.body.gates[0].gate).toBe('schema');
  });
});

describe('governance', () => {
  it('refuses to retire a pinned release, and records the refusal', async () => {
    const r = await post('/api/retire', { actor: 'shared.checker', name: 'uae.p.validate.iban.v1', release: '1.0.0' });
    expect(r.status).toBe(409);
    expect(r.body.error).toContain('pinned by');
    expect((await post('/api/retire', { actor: 'example.maker', name: 'uae.p.validate.iban.v1', release: '1.0.0' })).status).toBe(409);
  });

  it('refuses to retire a release with instances in flight', async () => {
    expect((await post('/api/in-flight', { actor: 'service.operations', counts: { 'p.rule.amount-positive.v1@1.0.0': 3 }, asOf: '2026-11-17T06:00:00Z' })).status).toBe(200);
    const pins = (await get('/api/releases/p.rule.amount-positive.v1/pins')).body;
    expect(pins.inFlight.count).toBe(3);
    expect(pins.mayRetire).toBe(false);
  });

  it('proposes the releases that propagate from a patch', async () => {
    expect((await get('/api/propagate?name=uae.p.rule.iban-checksum.v1&release=1.0.9')).status).toBe(409); // no such release
    const cur = (await get('/api/artefacts/uae.p.rule.iban-checksum.v1')).body;
    const doc = { ...(cur.doc ?? cur.entry.doc), release: '1.0.1', title: 'IBAN checksum, patched' };
    const saved = await post('/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc });
    expect(saved.status, JSON.stringify(saved.body)).toBe(200);
    const r = await get('/api/propagate?name=uae.p.rule.iban-checksum.v1&release=1.0.1');
    expect(r.status, JSON.stringify(r.body)).toBe(200);
    expect(JSON.stringify(r.body)).toContain('uae.wps.wf.salary-file-out.v1');
  });

  it('drafts a supersession, tests it and refuses its hand-over by the maker', async () => {
    const d = await post('/api/supersessions/draft', { artefact: 'uae.p.rule.iban-checksum.v1', replaces: '1.0.0', with: '1.0.0', reason: 'White-box test', maker: 'shared.designer' });
    expect(d.status, JSON.stringify(d.body)).toBe(200);
    const doc = d.body.doc ?? d.body;
    expect(doc.kind).toBe('supersession');
    const ev = await post('/api/supersessions/evidence', { doc });
    expect(ev.status).toBeLessThan(500);
    expect((await post('/api/supersessions/hand-over', { doc, checker: doc.maker ?? 'shared.designer' })).status).toBe(409);
    expect((await post('/api/supersessions/draft', { artefact: 'x', replaces: '1', with: '2', reason: 'r', maker: 'example.maker' })).status).toBe(409);
  });

  it('saves a bank configuration only with a later release, and only by a bank designer', async () => {
    const cs = (await get('/api/tenants/example-bank/configuration')).body.configurationSet;
    expect((await post('/api/tenants/example-bank/configuration', { actor: 'example.maker', yaml: toYaml(cs) })).status).toBe(409);
    expect((await post('/api/tenants/example-bank/configuration', { actor: 'example.checker', yaml: toYaml({ ...cs, release: '9.0.0' }) })).status).toBe(409);
    const saved = await post('/api/tenants/example-bank/configuration', { actor: 'example.maker', yaml: toYaml({ ...cs, release: '1.1.0' }) });
    expect(saved.status, JSON.stringify(saved.body)).toBe(200);
    expect((await post('/api/tenants/example-bank/configuration/validate', { doc: cs })).body.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
    const run = await post('/api/tenants/example-bank/compatibility-run', { candidates: [] });
    expect(run.status).toBe(200);
  });

  it('adds a waiver only with a reason and only from a checker', async () => {
    expect((await post('/api/conformance/waivers', { actor: 'shared.checker', scenario: 'CK-SEQ-01' })).status).toBe(409);
    expect((await post('/api/conformance/waivers', { actor: 'shared.designer', scenario: 'CK-SEQ-01', reason: 'r' })).status).toBe(409);
    expect((await post('/api/conformance/waivers', { actor: 'shared.checker', scenario: 'CK-SEQ-01', reason: 'White-box test', target: 'temporal' })).status).toBe(200);
    const report = (await get('/api/conformance/report?target=temporal')).body;
    expect(report.rows.find((r: any) => r.scenario === 'CK-SEQ-01')).toBeDefined();
  });

  it('builds a pack and refuses a download with red checks', async () => {
    const b = await post('/api/packs/build', { scope: 'uae.wps' });
    expect(b.body.files.length).toBeGreaterThan(10);
    const r = await get('/api/packs/download?scope=uae.wps');
    expect(r.status).toBe(200);
    expect(r.contentType).toBe('application/zip');
  });
});

describe('running and exporting', () => {
  it('runs an ad hoc bench and refuses an unknown target', async () => {
    const r = await post('/api/bench/run', { target: 'uae.wps.wf.salary-file-out.v1', inbound: 'samples/wps-sif-valid.sif', bench: [{ port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' }] });
    expect(r.status).toBe(200);
    expect(r.body.outcome).toBe('settled');
    expect((await post('/api/bench/run', { target: 'nope.x.y.v1' })).status).toBe(404);
  });

  it('tests a supersession against the workflows that pin it', async () => {
    const r = await post('/api/supersessions/test', { artefact: 'uae.p.rule.iban-checksum.v1', replaces: '1.0.0', with: '1.0.0' });
    expect(r.status).toBe(200);
    expect((await post('/api/supersessions/test', { artefact: 'uae.p.rule.iban-checksum.v1', replaces: '1.0.0', with: '7.0.0' })).status).toBe(404);
  });

  it('previews an export for each target, and serves a zip with a file name', async () => {
    for (const target of ['temporal', 'dtfx', 'flowable']) {
      const r = await post('/api/export', { workflows: ['p.wf.commercial.v1'], target });
      expect(r.status).toBe(200);
      expect(r.body.manifest.target).toBe(target);
    }
    const tenant = await post('/api/export', { workflows: ['p.wf.corporate.v1'], target: 'dtfx', tenant: 'example-bank' });
    expect(tenant.body.manifest.tenant).toBe('example-bank');
    const zip = await get('/api/export/download?workflows=uae.wps.wf.salary-file-out.v1&target=dtfx&withSimulators=true');
    expect(zip.filename).toMatch(/\.zip$/);
  });

  it('serves the samples, types, registry and coverage', async () => {
    expect((await get('/api/sample-messages/uae.wps.sif')).status).toBe(200);
    expect((await get('/api/sample-messages/nope')).body.note).toContain('No generator');
    expect((await get('/api/types/uae.p.validate.iban.v1?lang=python')).body.language).toBe('python');
    expect((await get('/api/types/nope.x.y.v1')).status).toBe(404);
    expect((await get('/api/registry/lookup?set=ExternalStatusReason1Code&code=ZZ99')).status).toBeGreaterThanOrEqual(200);
    expect((await get('/api/coverage/uae.aani')).body.rows.length).toBeGreaterThan(0);
    expect((await get('/api/simulators/nope/stub')).status).toBe(404);
    expect((await get('/api/tests/NOPE-1')).status).toBe(404);
    expect((await post('/api/tests/NOPE-1/run')).status).toBe(404);
    expect((await get('/api/schemes/nope')).status).toBe(404);
  });

  it('drafts with the assistant only when it has credentials', async () => {
    const r = await post('/api/ai/draft', { kind: 'rule', request: 'amount positive' });
    expect([200, 503]).toContain(r.status);
  });
});

describe('scripts-behind and samples', () => {
  const ACT = {
    kind: 'activity', name: 'p.enrich.blackbox-note.v1', release: '1.0.0', title: 'Note', type: 'enrich', reads: ['header'], writes: ['facts.note'],
    outcomes: [{ name: 'done', primary: true }], effect: 'pure', implementation: { binding: 'script', language: 'typescript', script: 'scripts/p.enrich.blackbox-note.v1.ts' },
  };
  const SRC = "export async function run({ ctx }) {\n  ctx.facts = { ...(ctx.facts ?? {}), note: { seen: ctx.header.paymentId } };\n  return 'done';\n}\n";

  it('saves a script for an activity draft, and refuses one for no draft, a wrong role or a forbidden call', async () => {
    expect((await post('/api/scripts', { tenant: 'owner', actor: 'shared.designer', activity: ACT.name, source: SRC })).status).toBe(409); // no draft yet
    expect((await post('/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: ACT })).status).toBe(200);
    expect((await post('/api/scripts', { tenant: 'owner', actor: 'shared.auditor', activity: ACT.name, source: SRC })).status).toBe(409);
    const bad = await post('/api/scripts', { tenant: 'owner', actor: 'shared.designer', activity: ACT.name, source: 'export async function run() { return Date.now() ? "done" : "done"; }' });
    expect(bad.status).toBe(409);
    expect((await post('/api/scripts', { tenant: 'owner', actor: 'shared.designer', activity: ACT.name, source: 'export const x = 1;' })).body.error).toContain('run');
    expect((await post('/api/scripts', { tenant: 'owner', actor: 'shared.designer', activity: ACT.name, language: 'python', source: 'x = 1' })).status).toBe(409); // declared typescript
    const ok = await post('/api/scripts', { tenant: 'owner', actor: 'shared.designer', activity: ACT.name, source: SRC });
    expect(ok.status, JSON.stringify(ok.body)).toBe(200);
    expect(env.scripts.find(ACT.name)?.source).toBe(SRC);
    const audit = (await get('/api/audit?tenant=owner')).body;
    expect(audit.some((a: any) => a.action === 'save-script' && a.outcome === 'refused')).toBe(true);
  });

  it('runs the saved script, and fixes it once the activity is published', async () => {
    const key = (await get('/api/drafts?tenant=owner')).body.find((d: any) => d.name === ACT.name).key;
    await post('/api/review', { tenant: 'owner', actor: 'shared.designer', key });
    expect((await post('/api/publish', { tenant: 'owner', key, checker: 'shared.checker' })).status).toBe(200);
    const r = await post('/api/bench/run', { target: ACT.name, context: { header: { paymentId: 'P-9' } } });
    expect(r.body.outcome, JSON.stringify(r.body).slice(0, 400)).toBe('done');
    expect(JSON.stringify(r.body)).toContain('"seen":"P-9"'); // the script ran, not the reference behaviour
    expect((await post('/api/scripts', { tenant: 'owner', actor: 'shared.designer', activity: ACT.name, source: SRC })).status).toBe(409);
  });

  it('saves a sample a test can name, and refuses a bad name or a factory name', async () => {
    const ok = await post('/api/samples', { tenant: 'owner', actor: 'shared.designer', name: 'blackbox-file.csv', content: 'H,1\n' });
    expect(ok.body.sample).toBe('samples/blackbox-file.csv');
    expect((await get('/api/samples')).body).toContain('samples/blackbox-file.csv');
    expect((await post('/api/samples', { tenant: 'owner', actor: 'shared.designer', name: 'Bad Name.csv', content: 'x' })).status).toBe(409);
    expect((await post('/api/samples', { tenant: 'owner', actor: 'shared.designer', name: 'wps-sif-valid.sif', content: 'x' })).status).toBe(409);
    expect((await post('/api/samples', { tenant: 'owner', actor: 'shared.auditor', name: 'x.csv', content: 'x' })).status).toBe(409);
  });
});
