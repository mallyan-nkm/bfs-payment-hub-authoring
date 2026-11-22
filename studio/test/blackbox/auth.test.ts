import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { parseYaml, toYaml } from '../../src/core/yaml.js';
import { client, signIn, startServer, type Server } from './server.js';

// Black box: sign-in and the permission table over HTTP (R-11-29 to R-11-33). The studio runs with
// the built-in development provider (TH_AUTH_DEV=1); every call is signed in as the user it names.

let server: Server;
beforeAll(async () => {
  server = await startServer();
}, 180000);
afterAll(() => server?.stop());

const bump = (r: string, part: 0 | 1 | 2 = 2) => r.split('.').map((x, i) => (i === part ? Number(x) + 1 : i > part ? 0 : Number(x))).join('.');
const raw = (p: string, init: RequestInit = {}) => fetch(server.url + p, { redirect: 'manual', ...init });

describe('sign-in (R-11-32, R-11-33)', () => {
  it('answers 401 to a call with no session, and the sign-in starts an authorization code flow with PKCE', async () => {
    expect((await raw('/api/catalogue')).status).toBe(401);
    expect((await raw('/auth/me')).status).toBe(401);
    expect((await raw('/api/drafts', { method: 'POST', body: '{}', headers: { 'content-type': 'application/json' } })).status).toBe(401);
    const login = await raw('/auth/login');
    expect(login.status).toBe(302);
    const to = new URL(login.headers.get('location')!);
    expect(to.pathname).toBe('/auth/dev/authorize');
    expect(to.searchParams.get('response_type')).toBe('code');
    expect(to.searchParams.get('code_challenge_method')).toBe('S256');
    expect(to.searchParams.get('code_challenge')).toMatch(/^[A-Za-z0-9_-]{43}$/);
    const cookie = login.headers.getSetCookie().join(';');
    expect(cookie).toMatch(/HttpOnly/);
    expect(cookie).toMatch(/SameSite=Lax/);
    // A callback the studio did not start is refused.
    expect((await raw('/auth/callback?code=x&state=y')).status).toBe(400);
  });

  it('signs in, sets a signed http-only session cookie, and signs out', async () => {
    const s = await signIn(server.url, 'example.maker');
    expect(s.me).toMatchObject({ signedIn: true, user: 'example.maker', tenant: 'example-bank', roles: ['bank configurator'], landing: 'bank' });
    expect(s.me.surfaces).toContain('bank');
    expect(s.me.surfaces).not.toContain('kit');
    expect((await raw('/api/me', { headers: { cookie: s.cookie } })).status).toBe(200);
    // A tampered cookie is no session.
    const forged = s.cookie.replace(/th_session=([^.;]+)/, 'th_session=forged');
    expect((await raw('/api/me', { headers: { cookie: forged } })).status).toBe(401);
    // Sign-out needs the CSRF token, then the session is gone.
    expect((await raw('/auth/logout', { method: 'POST', headers: { cookie: s.cookie } })).status).toBe(403);
    const out = await raw('/auth/logout', { method: 'POST', headers: { cookie: s.cookie, 'x-csrf-token': s.csrf } });
    expect(out.status).toBe(200);
    expect(out.headers.getSetCookie().join(';')).toMatch(/th_session=;.*Max-Age=0/);
    expect((await raw('/api/me', { headers: { cookie: s.cookie } })).status).toBe(401);
  });

  it('refuses a change without the CSRF token, from another origin, or naming another actor', async () => {
    const s = await signIn(server.url, 'shared.designer');
    const body = JSON.stringify({ key: 'x' });
    expect((await raw('/api/review', { method: 'POST', body, headers: { cookie: s.cookie, 'content-type': 'application/json' } })).status).toBe(403);
    expect((await raw('/api/review', { method: 'POST', body, headers: { cookie: s.cookie, 'content-type': 'application/json', 'x-csrf-token': 'wrong' } })).status).toBe(403);
    expect((await raw('/api/review', { method: 'POST', body, headers: { cookie: s.cookie, 'content-type': 'application/json', 'x-csrf-token': s.csrf, origin: 'https://elsewhere.example' } })).status).toBe(403);
    // The acting user is the session's, never the body's.
    const r = await raw('/api/review', { method: 'POST', body: JSON.stringify({ key: 'x', actor: 'shared.checker' }), headers: { cookie: s.cookie, 'content-type': 'application/json', 'x-csrf-token': s.csrf } });
    expect(r.status).toBe(403);
    expect((await r.json()).error).toMatch(/signed-in user, shared\.designer/);
  });

  it('gives an identity that is no user of the studio no rights', async () => {
    const s = await signIn(server.url, 'someone.unknown');
    expect(s.me).toMatchObject({ signedIn: true, identity: 'someone.unknown', surfaces: [] });
    expect(s.me.user).toBeUndefined();
    expect((await raw('/api/catalogue', { headers: { cookie: s.cookie } })).status).toBe(403);
  });
});

describe('a Bank Configurator (R-11-30)', () => {
  const bank = () => client(server.url, { as: 'example.maker' });

  it('gets 403 creating a workflow, a platform rule or a scheme, or changing a shared activity', async () => {
    const api = bank();
    const wf = (await api.post('/api/new', { kind: 'workflow', wfKind: 'platform', name: 'b.wf.own-flow.v1', from: 'template' })).data.doc;
    expect((await api.post('/api/drafts', { doc: wf })).status).toBe(403);
    const sharedWf = { ...wf, name: 'p.wf.bank-made.v1' };
    expect((await api.post('/api/drafts', { doc: sharedWf })).status).toBe(403);
    const rule = (await api.get('/api/artefacts/uae.p.rule.iban-length.v1')).data.doc;
    const r = await api.post('/api/drafts', { doc: { ...rule, release: bump(rule.release) } });
    expect(r.status).toBe(403);
    expect(r.data.error).toMatch(/scope b/);
    const scheme = (await api.get('/api/schemes/uae.wps')).data.scheme;
    expect((await api.post('/api/drafts', { doc: { ...scheme, release: bump(scheme.release) } })).status).toBe(403);
    const port = (await api.get('/api/artefacts/b.port.treasury.v1')).data.doc;
    expect((await api.post('/api/drafts', { doc: { ...port, release: bump(port.release) } })).status).toBe(403);
    // The refusals are audited.
    const audit = (await api.get('/api/audit')).data;
    expect(audit.filter((a: any) => a.outcome === 'refused' && a.actor === 'example.maker').length).toBeGreaterThanOrEqual(4);
  });

  it('gets 403 touching another bank', async () => {
    const api = bank();
    expect((await api.get('/api/tenants/second-bank/configuration')).status).toBe(403);
    expect((await api.get('/api/catalogue?tenant=second-bank')).status).toBe(403);
    const sim = (await api.get('/api/artefacts/b.sim.treasury.v1')).data.doc;
    expect((await api.post('/api/drafts', { tenant: 'second-bank', doc: { ...sim, release: bump(sim.release) } })).status).toBe(403);
    expect((await api.post('/api/bench/run', { target: 'p.wf.corporate.v1', tenant: 'second-bank' })).status).toBe(403);
  });

  it('unbinds and binds a hook and changes a dial within bounds; a bank approver who is not the maker makes each a version', async () => {
    const api = bank();
    const approver = client(server.url, { as: 'example.checker' });
    const cs = (await api.get('/api/tenants/example-bank/configuration')).data.configurationSet;
    const point = 'p.wf.corporate.v1#on-close';
    const binding = cs.bindings.find((b: any) => b.hookPoint === point);
    // A value outside the owner's bounds is refused.
    const tooHigh = { ...cs, release: bump(cs.release, 1), values: cs.values.map((v: any) => (v.parameter === 'b.rule.internal-limit.v1#internalLimit' && v.validFrom === '2027-01-01' ? { ...v, value: 5000000 } : v)) };
    expect((await api.post('/api/tenants/example-bank/configuration', { yaml: toYaml(tooHigh) })).status).toBe(409);
    // Unbind, with a dial inside its bounds.
    const unbound = { ...cs, release: bump(cs.release, 1), bindings: cs.bindings.filter((b: any) => b.hookPoint !== point), values: cs.values.map((v: any) => (v.parameter === 'b.rule.internal-limit.v1#internalLimit' && v.validFrom === '2027-01-01' ? { ...v, value: 120000 } : v)) };
    const d1 = await api.post('/api/tenants/example-bank/configuration', { yaml: toYaml(unbound) });
    expect(d1.status, JSON.stringify(d1.data)).toBe(200);
    expect(d1.data.draft.status).toBe('draft');
    // The maker may not approve, and nor may a platform approver.
    expect((await api.post('/api/publish', { key: d1.data.draft.key })).status).toBe(403);
    expect((await client(server.url, { as: 'shared.checker' }).post('/api/publish', { key: d1.data.draft.key })).status).toBe(404);
    const p1 = await approver.post('/api/publish', { key: d1.data.draft.key });
    expect(p1.status, JSON.stringify(p1.data)).toBe(200);
    let now = (await api.get('/api/tenants/example-bank/configuration')).data;
    expect(now.configurationSet.release).toBe(unbound.release);
    expect(now.hookPoints.find((h: any) => h.hookPoint === point).bindings).toEqual([]);
    // Bind it again.
    const rebound = { ...unbound, release: bump(unbound.release, 1), bindings: [...unbound.bindings, binding] };
    const d2 = await api.post('/api/tenants/example-bank/configuration', { yaml: toYaml(rebound) });
    expect(d2.status, JSON.stringify(d2.data)).toBe(200);
    expect((await approver.post('/api/publish', { key: d2.data.draft.key })).status).toBe(200);
    now = (await api.get('/api/tenants/example-bank/configuration')).data;
    expect(now.hookPoints.find((h: any) => h.hookPoint === point).bindings.map((b: any) => b.hook)).toEqual([binding.hook]);
    expect(now.parameters.find((p: any) => p.parameter === 'b.rule.internal-limit.v1#internalLimit').values.map((v: any) => v.value)).toContain(120000);
  });

  it('edits a simulator and a bank rule without approval, and runs the test bench on a configurable workflow', async () => {
    const api = bank();
    const sim = (await api.get('/api/artefacts/b.sim.treasury.v1')).data.doc;
    const d = await api.post('/api/drafts', { doc: { ...sim, release: bump(sim.release, 1), title: `${sim.title ?? sim.name}, edited` } });
    expect(d.status, JSON.stringify(d.data).slice(0, 300)).toBe(200);
    expect(d.data.draft.status).toBe('draft');
    const rule = (await api.get('/api/artefacts/b.rule.internal-limit.v1')).data.doc;
    expect((await api.post('/api/drafts', { doc: { ...rule, release: bump(rule.release), description: 'Edited by the bank' } })).status).toBe(200);
    const rr = await api.post('/api/request-release', { key: d.data.draft.key });
    expect(rr.status, JSON.stringify(rr.data)).toBe(200);
    const run = await api.post('/api/bench/run', { target: 'p.wf.corporate.v1' });
    expect(run.status, JSON.stringify(run.data).slice(0, 300)).toBe(200);
    expect(run.data.id).toBe('BENCH-RUN');
    const auditor = client(server.url, { as: 'example.auditor' });
    expect((await auditor.post('/api/bench/run', { target: 'p.wf.corporate.v1' })).status).toBe(403);
  });
});

describe('the Workflow Designer and the Workflow Developer (R-11-30)', () => {
  it('a designer composes a workflow but builds no activity; a developer the reverse; a platform approver releases', async () => {
    const designer = client(server.url, { as: 'workflow.designer' });
    const developer = client(server.url, { as: 'workflow.developer' });
    const wf = parseYaml((await designer.post('/api/new', { kind: 'workflow', wfKind: 'platform', name: 'p.wf.role-test.v1', from: 'template' })).data.yaml);
    expect((await designer.post('/api/drafts', { doc: wf })).status).toBe(200);
    expect((await developer.post('/api/drafts', { doc: { ...wf, name: 'p.wf.role-test-2.v1' } })).status).toBe(403);
    const act = { kind: 'activity', name: 'p.enrich.role-test.v1', release: '1.0.0', title: 'Role test', type: 'enrich', reads: ['header'], writes: ['facts.roleTest'], outcomes: [{ name: 'done', primary: true }], effect: 'pure', implementation: { binding: 'builtin', service: 'noop' }, provenance: { origin: 'authored', status: 'provisional' } };
    expect((await designer.post('/api/drafts', { doc: act })).status).toBe(403);
    const a = await developer.post('/api/drafts', { doc: act });
    expect(a.status).toBe(200);
    expect((await developer.post('/api/publish', { key: a.data.draft.key })).status).toBe(403);
    expect((await client(server.url, { as: 'example.checker' }).post('/api/publish', { key: a.data.draft.key })).status).toBe(404);
    const p = await client(server.url, { as: 'shared.checker' }).post('/api/publish', { key: a.data.draft.key });
    expect(p.status, JSON.stringify(p.data).slice(0, 300)).toBe(200);
  });

  it('keeps users and roles with the administrators, under maker-checker', async () => {
    const admin = client(server.url, { as: 'users.admin' });
    const list = (await admin.get('/api/users')).data;
    expect((await client(server.url, { as: 'shared.checker' }).post('/api/users/proposals', { users: list })).status).toBe(403);
    const p = await admin.post('/api/users/proposals', { users: [...list, { id: 'third.configurator', tenant: 'third-bank', roles: ['bank configurator'] }] });
    expect(p.status, JSON.stringify(p.data)).toBe(200);
    expect((await admin.post(`/api/users/proposals/${p.data.changeSet}/approve`)).status).toBe(409);
    expect((await client(server.url, { as: 'users.approver' }).post(`/api/users/proposals/${p.data.changeSet}/approve`)).status).toBe(200);
  });
});
