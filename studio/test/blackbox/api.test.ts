import { createHash } from 'node:crypto';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { client, startServer, type Server } from './server.js';
import { unzip } from '../../src/export/zip.js';

// Black box: the studio's HTTP API as a client sees it, on a fresh data root. Nothing here imports
// the studio's code.

let server: Server;
let api: ReturnType<typeof client>;
beforeAll(async () => {
  server = await startServer();
  api = client(server.url);
}, 60000);
afterAll(() => server?.stop());

const sha = (b: Buffer) => createHash('sha256').update(b).digest('hex');

describe('reading', () => {
  const GETS = [
    'meta', 'catalogue?q=iban', 'artefacts/uae.p.validate.iban.v1', 'schemes', 'schemes/uae.wps', 'data', 'checks', 'drafts', 'audit', 'tests', 'tests/WPS-OUT-001',
    'simulators', 'samples', 'kit', 'tenants/example-bank/configuration', 'supersessions', 'model', 'types/uae.p.validate.iban.v1', 'registry',
    'registry/lookup?set=ExternalStatusReason1Code&code=AM04', 'expanded/uae.wps.wf.salary-file-out.v1', 'packs', 'packs/uae.wps/manifest', 'packs/uae.wps/release-gate',
    'users', 'permit?user=example.maker&action=author&kind=hook&scope=b', 'permissions', 'me', 'audit/verify', 'releases/uae.p.validate.iban.v1/pins', 'supersessions/handed-over',
    'tenants/example-bank/settable', 'tenants/example-bank/export', 'conformance/report', 'coverage/uae.wps', 'test-coverage', 'test-coverage/uae.wps.wf.salary-file-out.v1',
    'sample-messages', 'bindings', 'ai/status',
  ];
  it.each(GETS)('GET /api/%s answers 200 with JSON', async (p) => {
    const r = await api.get(`/api/${p}`);
    expect(r.status).toBe(200);
    expect(Buffer.isBuffer(r.data)).toBe(false);
  });

  it('answers live and ready (R-10-09): ready once it has read the content branch', async () => {
    const live = await fetch(server.url + '/health/live');
    expect(live.status).toBe(200);
    expect((await live.json()).status).toBe('live');
    const ready = await fetch(server.url + '/health/ready');
    expect(ready.status).toBe(200);
    const body = await ready.json();
    expect(body.status).toBe('ready');
    expect(body.content).toMatch(/^[0-9a-f]{12}$/);
  });

  it('serves the web app and answers 404 for an unknown route or artefact', async () => {
    const home = await fetch(server.url + '/');
    expect([200, 404]).toContain(home.status); // 404 only when the web app has not been built
    expect((await api.get('/api/no-such-route')).status).toBe(404);
    const missing = await api.get('/api/artefacts/nope.x.y.v1');
    expect(missing.status).toBe(404);
    expect(missing.data.error).toContain('nope.x.y.v1');
  });

  it('describes the content it was started on', async () => {
    const meta = (await api.get('/api/meta')).data;
    expect(meta.tenants).toEqual(expect.arrayContaining(['owner', 'example-bank']));
    expect(meta.counts.activity).toBeGreaterThan(40);
    const cat = (await api.get('/api/catalogue?q=iban')).data.items;
    expect(cat.every((i: any) => JSON.stringify(i).toLowerCase().includes('iban'))).toBe(true);
    const packs = (await api.get('/api/packs')).data;
    expect(packs.filter((p: any) => p.scope !== 'b').every((p: any) => p.equal)).toBe(true);
    const bindings = (await api.get('/api/bindings')).data;
    expect(bindings.every((b: any) => b.equal)).toBe(true);
  });

  it('looks up a code in the ISO registry and says when it does not know one', async () => {
    expect((await api.get('/api/registry/lookup?set=ExternalStatusReason1Code&code=AM04')).data.name).toBe('Insufficient funds');
    const unknown = await api.get('/api/registry/lookup?set=ExternalStatusReason1Code&code=ZZ99');
    expect(unknown.status).toBeLessThan(500);
  });
});

describe('bad requests', () => {
  const POSTS = [
    'check', 'drafts', 'publish', 'review', 'bench/run', 'tenants/example-bank/configuration/check', 'supersessions/test', 'export', 'gates', 'expanded', 'analyse',
    'supersessions/evidence', 'supersessions/hand-over', 'tenants/example-bank/configuration/validate', 'retire', 'in-flight', 'supersessions/draft',
    'tenants/example-bank/configuration', 'conformance/waivers',
  ];
  it.each(POSTS)('POST /api/%s with an empty body is refused, not a server error', async (p) => {
    const r = await api.post(`/api/${p}`, {});
    expect(r.status).toBeLessThan(500);
    if (r.status >= 400) expect(typeof r.data.error).toBe('string');
  });

  it('refuses malformed JSON, unparseable YAML and YAML that is not a document', async () => {
    const r = await fetch(server.url + '/api/check', { method: 'POST', headers: { 'content-type': 'application/json', ...(await api.headers()) }, body: '{nope' });
    expect(r.status).toBe(400);
    // The editor's check reports a parse error as an issue, so that it can show it as the designer types.
    expect((await api.post('/api/check', { yaml: 'a: [1, 2' })).data.issues[0]).toMatchObject({ code: 'L00', severity: 'error' });
    expect((await api.post('/api/drafts', { tenant: 'owner', actor: 'shared.designer', yaml: 'a: [1, 2' })).status).toBe(400);
    expect((await api.post('/api/drafts', { tenant: 'owner', actor: 'shared.designer', yaml: '- just\n- a list' })).status).toBe(400);
  });

  it('refuses an export with no workflow, an unknown workflow or an unknown target', async () => {
    expect((await api.get('/api/export/download')).status).toBe(400);
    expect((await api.get('/api/export/download?workflows=nope.wf.x.v1&target=dtfx')).data.error).toContain('does not resolve');
    expect((await api.get('/api/export/download?workflows=p.wf.commercial.v1&target=nope')).data.error).toContain('No engine binding');
    expect((await api.get('/api/propagate')).status).toBe(400);
  });
});

describe('roles, tenancy and the audit log', () => {
  const RULE = (name: string, title = 'Black-box rule') => ({
    kind: 'rule', name, release: '1.0.0', title, ruleKind: 'field', level: 'payment', repeatability: 'non-repeatable',
    when: [{ match: { fact: 'Payment', as: 'p', where: 'p.amount <= 0' } }],
    then: [{ yield: { finding: { severity: 'reject', path: 'payment.amount', reasonCode: 'AM12' } } }],
    examples: [{ name: 'passes', evaluationDate: '2026-11-17', given: { Payment: { amount: 1 } }, expect: { findings: [] } }, { name: 'fails', evaluationDate: '2026-11-17', given: { Payment: { amount: 0 } }, expect: { findings: [{ reasonCode: 'AM12' }] } }],
  });

  it('answers the permission question per role', async () => {
    // The permission table answers for a user, an action, a kind and a scope (R-11-30).
    const permit = async (user: string, action: string, kind: string, scope?: string) => (await api.get(`/api/permit?user=${user}&action=${action}&kind=${kind}${scope ? `&scope=${scope}` : ''}`)).data.allowed;
    expect(await permit('example.maker', 'author', 'hook', 'b')).toBe(true);
    expect(await permit('example.checker', 'approve', 'hook', 'b')).toBe(true);
    expect(await permit('example.maker', 'approve', 'hook', 'b')).toBe(false);
    expect(await permit('example.auditor', 'author', 'hook', 'b')).toBe(false);
    expect(await permit('example.auditor', 'run', 'export', 'b')).toBe(false);
    expect(await permit('example.maker', 'author', 'workflow', 'b')).toBe(false);
    expect(await permit('example.maker', 'author', 'rule', 'uae.p')).toBe(false);
    expect(await permit('workflow.designer', 'author', 'workflow', 'p')).toBe(true);
    expect(await permit('workflow.designer', 'author', 'activity', 'p')).toBe(false);
    expect(await permit('example.maker', 'publish', 'hook', 'b')).toBe(false); // not an action the studio knows
    expect((await api.get('/api/permit?user=nobody&action=author&kind=hook')).status).toBe(404);
  });

  it('keeps a bank draft inside its tenant', async () => {
    const saved = await api.post('/api/drafts', { tenant: 'example-bank', actor: 'example.maker', doc: RULE('b.rule.amount-check.v1') });
    expect(saved.status, JSON.stringify(saved.data)).toBe(200);
    const mine = (await api.get('/api/drafts?tenant=example-bank')).data.map((d: any) => d.name);
    expect(mine).toContain('b.rule.amount-check.v1');
    for (const t of ['second-bank', 'owner']) expect((await api.get(`/api/drafts?tenant=${t}`)).data.map((d: any) => d.name)).not.toContain('b.rule.amount-check.v1');
    const crossed = await api.post('/api/drafts', { tenant: 'example-bank', actor: 'second.maker', doc: RULE('b.rule.amount-check.v1') });
    expect(crossed.status).toBe(403);
  });

  it('publishes through maker and checker, once, and never in place', async () => {
    const name = 'p.rule.blackbox-amount.v1';
    const saved = await api.post('/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: RULE(name) });
    expect(saved.status, JSON.stringify(saved.data)).toBe(200);
    const key = saved.data.draft.key;
    expect((await api.post('/api/publish', { tenant: 'owner', key, checker: 'shared.designer' })).status).toBe(403); // a maker holds no approver right
    expect((await api.post('/api/publish', { tenant: 'owner', key, checker: 'shared.auditor' })).status).toBe(403); // not an approver
    expect((await api.post('/api/review', { tenant: 'owner', actor: 'shared.designer', key })).status).toBe(200);
    const pub = await api.post('/api/publish', { tenant: 'owner', key, checker: 'shared.checker' });
    expect(pub.status, JSON.stringify(pub.data)).toBe(200);
    expect((await api.get(`/api/artefacts/${name}`)).data.entry.release).toBe('1.0.0');
    // The same release with other content cannot be published again (R-01-10).
    const again = await api.post('/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: RULE(name, 'Changed in place') });
    const second = again.status === 200 ? await api.post('/api/publish', { tenant: 'owner', key: again.data.draft.key, checker: 'shared.checker' }) : again;
    expect(second.status).toBe(409);
  });

  it('records every write and refusal in a hash chain that verifies', async () => {
    const audit = (await api.get('/api/audit?tenant=owner')).data;
    expect(audit.some((a: any) => a.action === 'publish' && a.outcome === 'done')).toBe(true);
    expect(audit.some((a: any) => a.outcome === 'refused')).toBe(true);
    expect((await api.get('/api/audit/verify')).data.ok).toBe(true);
  });

  it('checks a configuration set against the tenant it names', async () => {
    const cs = (await api.get('/api/tenants/example-bank/configuration')).data.configurationSet;
    expect((await api.post('/api/tenants/example-bank/configuration/check', { doc: cs })).data.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
    const wrong = await api.post('/api/tenants/second-bank/configuration/check', { doc: cs });
    expect(wrong.data.issues.some((i: any) => i.code === 'R-01-07')).toBe(true);
  });
});

describe('running', () => {
  it('runs a factory test, the whole suite and the conformance kit', async () => {
    const one = await api.post('/api/tests/WPS-OUT-001/run');
    expect(one.data.passed).toBe(true);
    const all = (await api.post('/api/tests/run-all')).data;
    const results = Array.isArray(all) ? all : all.results;
    expect(results.length).toBeGreaterThanOrEqual(16);
    expect(results.filter((r: any) => !r.passed).map((r: any) => r.id)).toEqual([]);
    const kit = (await api.post('/api/kit/run')).data;
    expect(kit.length).toBe(50);
    expect(kit.filter((k: any) => !k.passed).map((k: any) => k.id)).toEqual([]);
  }, 120000);

  it('exports reproducibly: the same request gives the same package, signed each time (R-09-23)', async () => {
    const a = await api.get('/api/export/download?workflows=p.wf.commercial.v1&target=temporal');
    const b = await api.get('/api/export/download?workflows=p.wf.commercial.v1&target=temporal');
    expect(a.status).toBe(200);
    // Every file the same, byte for byte; an ECDSA signature differs each time, over the same statement of the same files.
    const content = (z: Buffer) => unzip(z).filter((e) => e.name !== 'release.sig' && e.name !== 'release.json').map((e) => `${e.name} ${sha(e.content)}`);
    expect(content(a.data)).toEqual(content(b.data));
    expect(JSON.parse(unzip(a.data).find((e) => e.name === 'release.json')!.content.toString()).files).toEqual(JSON.parse(unzip(b.data).find((e) => e.name === 'release.json')!.content.toString()).files);
    const withSims = await api.get('/api/export/download?workflows=p.wf.commercial.v1&target=temporal&withSimulators=true');
    expect(sha(withSims.data)).not.toBe(sha(a.data));
  });

  it('serves a deployable simulator stub', async () => {
    const r = await fetch(server.url + '/api/simulators/uae.wps.sim.wps-outward.v1/stub', { headers: await api.headers() });
    expect(r.status).toBe(200);
    expect(await r.text()).toMatch(/^#!\/usr\/bin\/env node/);
  });

  it('says plainly that the assistant has no credentials, rather than inventing a draft', async () => {
    const status = (await api.get('/api/ai/status')).data;
    if (status.configured) return;
    const r = await api.post('/api/ai/draft', { request: 'a rule that the amount is positive' });
    expect(r.status).toBe(503);
  });
});
