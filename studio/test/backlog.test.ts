import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { spawn } from 'node:child_process';
import { describe, expect, it } from 'vitest';
import { modelOf } from '../src/core/model.js';
import { typeCheckScript, typescriptTypes, pythonTypes } from '../src/core/types.js';
import { CodeRegistry } from '../src/core/registry.js';
import { compareManifest, buildPack } from '../src/governance/packs.js';
import * as gov from '../src/governance/governance.js';
import { reasonCodeCoverage } from '../src/sim/coverage.js';
import { stubSource } from '../src/sim/stub.js';
import { samplesFor } from '../src/samples/samples.js';
import { compareBindings } from '../src/export/bindings.js';
import { untestedOutcomes } from '../src/testing/coverage.js';
import { runTest } from '../src/testing/runner.js';
import { draft } from '../src/ai/assistant.js';
import { dispatchAs as dispatch } from './support/as-user.js';
import { toYaml } from '../src/core/yaml.js';
import { freshEnv } from './helpers.js';

describe('C03 canonical model, types and registries', () => {
  const env = freshEnv();
  const m = modelOf(env.store);
  it('resolves every path the factory reads or writes (C03-S1)', () => {
    for (const a of env.store.byKind('activity')) for (const p of [...(a.doc.reads ?? []), ...(a.doc.writes ?? [])]) expect(m.resolve(p), `${a.name} ${p}`).toBeDefined();
  });
  it('merges extensions and refuses an unregistered one (C03-S2)', () => {
    expect(m.resolve('ext.uae.wps.employerId')?.extension).toBe('uae.wps.ext.salary.v1');
    expect(m.resolve('items[].ext.uae.wps.personId')?.personalData).toBe(true);
    expect(m.resolve('ext.uae.unknown.field')).toBeUndefined();
  });
  it('generates types reproducibly and the type check refuses an undeclared path (C03-S3, S4)', () => {
    const a = env.store.doc('p.validate.duplicate.v1');
    expect(typescriptTypes(m, a)).toBe(typescriptTypes(m, a));
    expect(pythonTypes(m, a)).toContain('class Ctx(TypedDict, total=False)');
    expect(typeCheckScript(m, a, `export async function run({ ctx }) { return ctx.header.endToEndId ? 'unique' : 'duplicate'; }`, 'typescript')).toEqual([]);
    expect(typeCheckScript(m, a, `export async function run({ ctx }) { return ctx.payment.creditor.name; }`, 'typescript')[0].message).toMatch(/not in the declared reads/);
    expect(typeCheckScript(m, a, `def run(api):\n    return api.ctx['status']['code']\n`, 'python')[0].message).toMatch(/not in the declared reads/);
  });
  it('finds every reason code a factory rule uses in the registry, and names the version of an unknown one (C03-S5)', () => {
    const r = CodeRegistry.load(path.join(env.studioRoot, 'registries'));
    expect(r.reasonCodeReport(env.store.all().map((e) => e.doc), '2026-11-17').missing).toEqual([]);
    expect(() => r.lookup('ExternalStatusReason1Code', 'ZZ99', '2026-11-17')).toThrow(/registry version 2026-11-17.1/);
  });
});

describe('C05, C19 packs', () => {
  const env = freshEnv();
  it('regenerates the factory pack manifests exactly (C05-S2)', () => {
    for (const s of ['p', 'uae.p', 'uae.wps']) expect(compareManifest(env, s), s).toEqual({ equal: true, differences: [] });
  });
  it('keeps simulators out of a promotable pack (C19-S1)', () => {
    const shared = buildPack(env, 'uae.wps', 'shared');
    const test = buildPack(env, 'uae.wps', 'test');
    expect(shared.manifest.promotable).toBe(true);
    expect(shared.manifest.artefacts.some((a: any) => a.kind === 'simulator')).toBe(false);
    expect(test.manifest.containsSimulators).toBe(true);
    expect(test.manifest.promotable).toBe(false);
  });
  it('regenerates the three engine bindings (C20-S1, C21-S4)', () => {
    expect(compareBindings(env).every((b) => b.equal)).toBe(true);
  });
});

describe('C17 roles, maker-checker, audit and isolation', () => {
  it('refuses a bank designer outside scope b with the rule named (C17-S2, S3)', () => {
    const env = freshEnv();
    expect(gov.permit(env, 'example.maker', 'example-bank', 'author', { kind: 'hook', scope: 'uae.wps' })).toMatch(/R-06-03/);
    expect(gov.permit(env, 'example.maker', 'example-bank', 'author', { kind: 'hook', scope: 'b' })).toBeUndefined();
  });
  it('refuses a maker who approves their own publish, and logs the attempt in a chained audit (C17-S4)', async () => {
    const env = freshEnv();
    const doc = { ...env.store.doc('uae.p.rule.iban-length.v1'), release: '1.0.1' };
    const d = env.store.saveDraft('owner', 'shared.designer', doc);
    await expect(gov.approve(env, 'owner', 'shared.designer', d.key)).rejects.toThrow();
    expect(env.store.audit.some((a) => a.outcome === 'refused' && a.actor === 'shared.designer')).toBe(true);
    const p = await gov.approve(env, 'owner', 'shared.checker', d.key);
    expect(p.status).toBe('published');
    expect(env.store.audit.find((a) => a.action === 'publish' && a.outcome === 'done')?.diff).toBeDefined();
    expect((await env.store.verifyAudit()).ok).toBe(true);
    env.store.audit[0].actor = 'someone-else';
    expect((await env.store.verifyAudit()).ok).toBe(false);
  });
  it('lets two banks author the same b name with different content, each seeing only its own (C17-S1, S5)', async () => {
    const env = freshEnv();
    const base = env.store.doc('b.hook.pre-release-approval.v1');
    env.store.saveDraft('example-bank', 'example.maker', { ...base, release: '1.1.0', title: 'Example bank version' });
    env.store.saveDraft('second-bank', 'second.maker', { ...base, release: '1.1.0', title: 'Second bank version' });
    const cat = async (tenant: string) => (await dispatch(env, { method: 'GET', path: '/api/catalogue', query: { tenant, q: 'pre-release' }, body: undefined })).body.items.map((i: any) => i.title);
    expect(await cat('example-bank')).toContain('Example bank version');
    expect(await cat('example-bank')).not.toContain('Second bank version');
    expect(await cat('second-bank')).toContain('Second bank version');
    expect(await cat('second-bank')).not.toContain('Example bank version');
    const audit = (await dispatch(env, { method: 'GET', path: '/api/audit', query: { tenant: 'second-bank' }, body: undefined })).body;
    expect(audit.every((a: any) => a.tenant === 'second-bank')).toBe(true);
    const other = await dispatch(env, { method: 'GET', path: '/api/artefacts/b.hook.pre-release-approval.v1', query: { tenant: 'second-bank', key: 'example-bank/b.hook.pre-release-approval.v1@1.1.0#draft' }, body: undefined });
    expect(other.status).toBe(404);
    const write = await dispatch(env, { method: 'POST', path: '/api/drafts', query: {}, body: { tenant: 'second-bank', actor: 'example.maker', doc: { ...base, release: '1.2.0' } } });
    expect(write.status).toBe(403);
    expect(env.store.audit.some((a) => a.outcome === 'refused' && a.actor === 'example.maker' && a.tenant === 'second-bank')).toBe(true);
  });
});

describe('C19 retirement, propagation and supersession; C18 configuration', () => {
  it('refuses to retire a pinned release (C19-S3)', () => {
    const env = freshEnv();
    // The release the lock files pin (1.1.0 since factory 1.23.0, R-09-283).
    expect(() => gov.retire(env, 'service.operations', 'p.reserve.funds.v1', env.store.doc('p.reserve.funds.v1').release)).toThrow(/pinned by .*p.wf.corporate.v1/);
  });
  it('proposes patch releases of the workflows that pin a patched rule (C05-S4)', async () => {
    const env = freshEnv();
    const d = env.store.saveDraft('owner', 'shared.designer', { ...env.store.doc('uae.p.rule.iban-checksum.v1'), release: '1.0.1' });
    env.store.publish('owner', d.key, 'shared.designer', 'shared.checker');
    const p = await gov.propagate(env, 'uae.p.rule.iban-checksum.v1', '1.0.1');
    const by = Object.fromEntries(p.workflows.map((w: any) => [w.workflow, w.proposed]));
    // A patch of each workflow's own release: the salary-file leg is at 1.3.0 since the party register lookup (R-12-27).
    expect(by['uae.wps.wf.salary-file-out.v1']).toBe('1.4.1');
    expect(Object.keys(by).every((w) => by[w] === (w === 'uae.wps.wf.salary-file-out.v1' ? '1.4.1' : '1.0.1'))).toBe(true);
  });
  it('rebuilds a supersession, reruns it green, and hands it over with two approvers (C19-S4, S5)', async () => {
    const env = freshEnv();
    const d = env.store.saveDraft('owner', 'shared.designer', { ...env.store.doc('uae.p.rule.iban-checksum.v1'), release: '1.0.1' });
    env.store.publish('owner', d.key, 'shared.designer', 'shared.checker');
    let s = gov.draftSupersession(env, { artefact: 'uae.p.rule.iban-checksum.v1', replaces: '1.0.0', with: '1.0.1', reason: 'Correct the check-digit rule', maker: 'shared.designer' });
    expect(s.scope.workflowReleases).toContain('uae.wps.wf.salary-file-out.v1@1.0.0');
    s = await gov.testSupersession(env, s);
    expect(s.evidence.testRuns.every((r: any) => r.result === 'green')).toBe(true);
    const h = gov.handOverSupersession(env, s, 'shared.checker');
    expect(env.schemas.validate(h.document)).toEqual([]);
    expect(h.document.approval.maker).not.toBe(h.document.approval.checker);
  });
  it('refuses a value above its bound and overlapping periods (C18-S2)', () => {
    const env = freshEnv();
    const cs = env.store.configurationSet('example-bank');
    const bad = { ...cs, values: [...cs.values, { parameter: 'b.rule.internal-limit.v1#internalLimit', value: 2000000, validFrom: '2026-06-01' }] };
    const msgs = gov.checkConfiguration(env, 'example-bank', bad).map((i) => i.message).join(' | ');
    expect(msgs).toMatch(/outside its bounds/);
    expect(msgs).toMatch(/overlaps/);
    expect(gov.checkConfiguration(env, 'example-bank', cs).filter((i) => i.severity === 'error')).toEqual([]);
  });
  it('lists a bound hook as incompatible when a new segment removes its hook point (C18-S3)', async () => {
    const env = freshEnv();
    const seg = { ...env.store.doc('p.seg.check-and-screen.v1'), release: '2.0.0', hookPoints: [] };
    const r = await gov.compatibilityRun(env, 'example-bank', [seg]);
    expect(r.accepted).toBe(false);
    expect(r.bindings.find((b: any) => b.hookPoint.includes('enrich-before-screen')).compatible).toBe(false);
  });
});

describe('C10 simulators, C11 tests, C12 samples', () => {
  it('fails a simulator self-check on a bad file unless the scenario is malformed (C10-S3)', async () => {
    const env = freshEnv();
    const base: any = { kind: 'test', id: 'SELF-1', target: 'p.wf.corporate.v1', level: 'end-to-end', evaluationDate: '2026-11-17', expect: {} };
    const bad = await runTest(env, { ...base, bench: [{ port: 'uae.wps.port.employer-channel.v1', simulator: 'uae.wps.sim.employer-channel.v1', scenario: 'valid-file' }], given: { inbound: 'samples/wps-sif-bad-control-total.sif' } });
    expect(bad.checks.find((c) => c.what.startsWith('self-check'))?.actual).toEqual(['AM10']);
    const exempt = await runTest(env, { ...base, bench: [{ port: 'uae.wps.port.employer-channel.v1', simulator: 'uae.wps.sim.employer-channel.v1', scenario: 'bad-control-total' }], given: { inbound: 'samples/wps-sif-bad-control-total.sif' } });
    expect(exempt.checks.some((c) => c.what.startsWith('self-check'))).toBe(false);
  });
  it('reports reason-code coverage: AM10 covered for uae.wps (C10-S4)', () => {
    const r = reasonCodeCoverage(freshEnv(), 'uae.wps');
    expect(r.covered).toContain('AM10');
    expect(r.open.length).toBeGreaterThan(0);
  });
  it('runs the deployable stub of p.sim.core-banking.v1 outside the studio (C10-S5)', async () => {
    const env = freshEnv();
    const f = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'stub-')), 'stub.cjs');
    fs.writeFileSync(f, stubSource(env.store.doc('p.sim.core-banking.v1')));
    const port = 7300 + Math.floor(Math.random() * 500);
    const child = spawn(process.execPath, [f, '--port', String(port)]);
    try {
      await new Promise((r) => setTimeout(r, 500));
      const post = (u: string, b: any) => fetch(`http://127.0.0.1:${port}${u}`, { method: 'POST', body: JSON.stringify(b) });
      await post('/scenario', { scenario: 'reject' });
      const r = await (await post('/message', { id: 'm1', type: 'p.core.request' })).json();
      expect(r.message.status).toBe('rejected');
      await post('/scenario', { scenario: 'fail-twice-then-accept' });
      expect((await post('/message', { id: 'm2' })).status).toBe(503);
      expect((await post('/message', { id: 'm2' })).status).toBe(503);
      expect((await (await post('/message', { id: 'm2' })).json()).message.status).toBe('accepted');
    } finally {
      child.kill();
    }
  });
  it('reports the salary-file leg as lacking a test for returned (C11-S5)', () => {
    expect(untestedOutcomes(freshEnv(), 'uae.wps.wf.salary-file-out.v1')?.untested).toEqual(['returned']);
  });
  it('generates valid and invalid samples, each proved (C12-S4, F-05)', async () => {
    const env = freshEnv();
    const r = await samplesFor(env, 'uae.wps.sif');
    expect(r.samples.length).toBeGreaterThan(3);
    for (const s of r.samples) expect(s.proof?.proved, s.name).toBe(true);
  });
});

describe('C16 AI authoring: the loop and the gates, with an injected drafter', () => {
  const rule = (failing: boolean) => ({
    kind: 'rule', name: 'uae.wps.rule.salary-month-present.v1', release: '1.0.0', title: 'Salary month is present', ruleKind: 'field', level: 'batch', repeatability: 'non-repeatable',
    when: [{ match: { fact: 'WpsBatch', as: 'w', where: 'isBlank(w.salaryMonth)' } }],
    then: [{ yield: { finding: { severity: 'reject', path: 'ext.uae.wps.salaryMonth', reasonCode: 'CH21' } } }],
    examples: [
      { name: 'passes', given: { WpsBatch: { salaryMonth: '102026' } }, expect: { findings: [] } },
      { name: 'fails', given: { WpsBatch: {} }, expect: { findings: [{ reasonCode: failing ? 'AM10' : 'CH21' }] } },
    ],
  });
  const drafter = (doc: any) => async () => ({ json: { artefactYaml: toYaml(doc), scriptSource: '', testsYaml: [], questions: ['Is the salary month mandatory on every file?'], clauses: ['Public SIF description, SCR record'] }, model: 'test-drafter' });
  it('passes every gate for a sound draft and records provenance (C16-S1, S5)', async () => {
    const env = freshEnv();
    const d = await draft(env, { kind: 'rule', instruction: 'The salary month must be present' }, drafter(rule(false)));
    expect(d.passed).toBe(true);
    expect(d.gates.map((g) => g.gate)).toEqual(['schema', 'static checks', 'tests', 'provenance']);
    expect(d.doc.provenance).toMatchObject({ origin: 'authored', clauses: ['Public SIF description, SCR record'], ai: { model: 'test-drafter' } });
    expect(d.doc.provenance.ai.promptRef).toMatch(/^sha256:/);
    expect(JSON.stringify(d.doc.provenance)).not.toContain('The salary month must be present');
    expect(d.questions.length).toBe(1);
  });
  it('stops at the tests gate for a draft with a failing example', async () => {
    const env = freshEnv();
    const d = await draft(env, { kind: 'rule', instruction: 'x' }, drafter(rule(true)));
    expect(d.passed).toBe(false);
    expect(d.firstRed?.gate).toBe('tests');
  });
});
