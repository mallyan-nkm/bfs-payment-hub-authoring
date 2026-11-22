import { parseYaml, toYaml } from '../core/yaml.js';
import { OWNER_TENANT, StoreError, keyOf } from '../core/store.js';
import { parseName } from '../core/names.js';
import { CONTEXT_PARTS, coveredByAny } from '../core/paths.js';
import { Checker, CHECK_CATALOGUE, checkAll, checkDocument, interfaceDiff, proposeRelease, refsOf, allSteps, stepLabel } from '../checks/checks.js';
import { analyseTable, runExamples } from '../rules/service.js';
import { effectivePolicy } from '../interp/policy.js';
import { missingStandardScenarios } from '../sim/runtime.js';
import { runTest, runAllTests, sampleNames, type TestRunReport } from '../testing/runner.js';
import { runKit, loadKit } from '../conformance/kit.js';
import { exportPackage, signedZip, type Target } from '../export/exporter.js';
import { SignatureRejected } from '../export/signing.js';
import * as gitExport from '../export/git.js';
import { ContentError } from '../content/content-store.js';
import { GitHubError } from '../content/github.js';
import { templates } from './templates.js';
import { runGates } from '../ai/gates.js';
import { assistantConfigured, draft } from '../ai/assistant.js';
import { agentTurn, anthropicAgentModel, replayModel, type AgentModel } from '../ai/agent.js';
import { explainBench, explainDoc } from '../explain/explain.js';
import { FIELD_HELP, fieldHelp } from '../explain/fields.js';
import { analyse as analyseImport, apply as applyImport, readBundle } from '../import/bundle.js';
import { bundleZip, wpsTrainingBundle } from '../import/sample.js';
import { modelOf } from '../core/model.js';
import { typescriptTypes, pythonTypes } from '../core/types.js';
import * as authoring from '../governance/authoring.js';
import { TEMPLATES } from '../ai/templates.js';
import { CodeRegistry, RegistryError } from '../core/registry.js';
import { expandWorkflow } from '../core/expand.js';
import { buildPack, compareManifest, packManifest, packZip } from '../governance/packs.js';
import * as gov from '../governance/governance.js';
import { enforce, submitForReview } from '../governance/governance.js';
import { reasonCodeCoverage } from '../sim/coverage.js';
import { stubSource } from '../sim/stub.js';
import { testCoverageReport, untestedOutcomes } from '../testing/coverage.js';
import { samplesFor, messagesWithSamples } from '../samples/samples.js';
import { compareBindings } from '../export/bindings.js';
import * as pathmod from 'node:path';
import type { StudioEnv } from '../env.js';
import { ROLES, effectiveRoles, landingOf, permissionRows, surfacesOf, type Action } from '../governance/permissions.js';

// The studio's HTTP API. Every call acts as the signed-in user (R-11-32): the server sets `user` from
// the session, never from the request's body or query. The tenant is the user's; a call that names
// another tenant is refused (R-06-01, R-10-05). Every write, publish and approval asks the permission
// table (R-11-30). The studio never touches a running payment (R-10-01): it has no connection to one.

export class HttpError extends Error {
  constructor(public status: number, message: string, public body?: any) {
    super(message);
  }
}

/** A call that names another tenant than the user's (R-06-01): 403, and audited when it would change something. */
class TenantRefusal extends HttpError {
  constructor(public tenant: string, message: string) {
    super(403, message);
  }
}

export interface Req {
  method: string;
  path: string;
  query: Record<string, string>;
  body: any;
  /** The signed-in user's id in governance/users.yaml, set by the server from the session. */
  user?: string;
  /** The identity the session signed in with, when it maps to no user of the studio. */
  identity?: string;
}

type Handler = (env: StudioEnv, req: Req, params: Record<string, string>) => Promise<any> | any;

const routes: { method: string; pattern: RegExp; keys: string[]; handler: Handler }[] = [];

function route(method: string, path: string, handler: Handler) {
  const keys: string[] = [];
  const pattern = new RegExp('^' + path.replace(/:(\w+)/g, (_, k) => (keys.push(k), '([^/]+)')) + '$');
  routes.push({ method, pattern, keys, handler });
}

export async function dispatch(env: StudioEnv, req: Req): Promise<{ status: number; body: any; contentType?: string; filename?: string }> {
  for (const r of routes) {
    if (r.method !== req.method) continue;
    const m = r.pattern.exec(req.path);
    if (!m) continue;
    const params = Object.fromEntries(r.keys.map((k, i) => [k, decodeURIComponent(m[i + 1])]));
    // Who is asking (R-11-32): nobody signed in is 401; an identity that is not a user of the studio,
    // or a user no longer in governance/users.yaml, is 403.
    if (!req.user) return req.identity ? { status: 403, body: { error: `${req.identity} is signed in but is not a user of the studio`, code: 'R-11-32' } } : { status: 401, body: { error: 'Sign in first', code: 'R-11-32' } };
    const who = gov.userById(env, req.user);
    if (!who) return { status: 403, body: { error: `${req.user} is not a user of the studio`, code: 'R-11-32' } };
    userTenant.set(req.user, who.tenant);
    if (params.tenant !== undefined && params.tenant !== who.tenant) return { status: 403, body: { error: `${req.user} works for tenant ${who.tenant}, not ${params.tenant} (R-06-01)`, code: 'R-06-01' } };
    try {
      const body = await r.handler(env, req, params);
      // A change is on the branch before the answer goes back (R-11-21): wait for its commits.
      if (req.method !== 'GET') await env.store.flush();
      if (body && body.__binary) return { status: 200, body: body.data, contentType: body.contentType, filename: body.filename };
      return { status: 200, body };
    } catch (e: any) {
      // A write that did not reach the branch leaves memory ahead of it: read the branch again.
      let wrote: any = e;
      try {
        await env.store.flush();
      } catch (w) {
        wrote = w;
      }
      if (wrote instanceof ContentError || wrote instanceof GitHubError) {
        await env.store.reload().catch(() => undefined);
        return { status: 409, body: { error: wrote.message, code: wrote instanceof ContentError ? wrote.code : 'github' } };
      }
      if (e instanceof TenantRefusal && req.method !== 'GET') {
        env.store.record({ tenant: e.tenant, actor: req.user, action: req.path === '/api/publish' ? 'approve' : 'author', key: req.path, outcome: 'refused', detail: e.message });
        await env.store.flush().catch(() => undefined);
      }
      if (e instanceof HttpError) return { status: e.status, body: { error: e.message, ...(e.body ?? {}) } };
      if (e instanceof gov.Forbidden) return { status: 403, body: { error: e.message, code: e.code } };
      if (e instanceof StoreError) return { status: 409, body: { error: e.message, code: e.code } };
      return { status: 500, body: { error: String(e?.message ?? e) } };
    }
  }
  return { status: 404, body: { error: `No route ${req.method} ${req.path}` } };
}

/** The tenant of each user, refreshed on every call so a change to the users applies at once. */
const userTenant = new Map<string, string>();

/** The signed-in user. A body or query that names another acting user is refused, not obeyed (R-11-32). */
function actorOf(req: Req): string {
  const user = req.user!;
  for (const k of ['actor', 'checker', 'maker']) {
    const named = req.body?.[k] ?? (k === 'actor' ? req.query.actor : undefined);
    if (named && named !== user) throw new HttpError(403, `The acting user is the signed-in user, ${user}; ${k} ${named} is not taken from a request (R-11-32)`);
  }
  return user;
}

/** The user's tenant. A call that names another tenant, in the query or the body, is refused (R-06-01). */
function tenantOf(req: Req): string {
  const u = req.user ? userTenant.get(req.user) : undefined;
  if (!u) throw new HttpError(401, 'Sign in first');
  for (const named of [req.query.tenant, req.body?.tenant]) {
    if (named && named !== u) throw new TenantRefusal(named, `${req.user} works for tenant ${u}, not ${named} (R-06-01)`);
  }
  return u;
}

/** Refuse unless the permission table allows it (R-11-30). */
function must(env: StudioEnv, req: Req, action: Action, key: string, target: gov.Target) {
  enforce(env, actorOf(req), tenantOf(req), action, key, target);
}
function mayRun(env: StudioEnv, req: Req, kind: 'bench' | 'import' | 'export' | 'deploy' | 'assistant', key: string = kind) {
  must(env, req, 'run', key, { kind, scope: tenantOf(req) === OWNER_TENANT ? 'p' : 'b' });
}

function visibleEntries(env: StudioEnv, tenant: string) {
  return env.store.all().filter((e) => e.tenant === OWNER_TENANT || e.tenant === tenant);
}

function typeOf(e: any): string {
  return e.doc?.type ?? e.doc?.wfKind ?? e.doc?.mode ?? e.doc?.hitPolicy ?? e.doc?.direction ?? '';
}

function summary(e: any) {
  return {
    key: e.key, name: e.name, kind: e.kind, type: typeOf(e), binding: e.doc?.implementation?.binding, scope: e.scope, release: e.release, title: e.doc?.title ?? e.doc?.id ?? e.name,
    status: e.status, tenant: e.tenant, provenance: e.doc?.provenance?.status, generated: e.generated, source: e.source,
  };
}

// ------------------------------------------------------------------- meta

route('GET', '/api/meta', (env, req) => {
  const tenant = tenantOf(req);
  const counts: Record<string, number> = {};
  for (const e of env.store.all()) counts[e.kind] = (counts[e.kind] ?? 0) + 1;
  return {
    // A bank sees its own tenant and the owner's; the owner sees every tenant's name.
    tenants: tenant === OWNER_TENANT ? env.store.tenants() : [OWNER_TENANT, tenant],
    owner: OWNER_TENANT,
    counts,
    checks: CHECK_CATALOGUE,
    contextParts: CONTEXT_PARTS,
    roles: ROLES,
    scripts: env.scripts.list(),
  };
});

// -------------------------------------------------------------- catalogue

route('GET', '/api/catalogue', (env, req) => {
  const tenant = tenantOf(req);
  const q = (req.query.q ?? '').toLowerCase();
  const kind = req.query.kind;
  const scope = req.query.scope;
  const type = req.query.type;
  const latest = new Map<string, any>();
  for (const e of visibleEntries(env, tenant)) {
    if (['inventory', 'conformance-kit'].includes(e.kind)) continue;
    const k = e.status === 'draft' ? e.key : e.name;
    const prev = latest.get(k);
    if (!prev || (e.release ?? '') > (prev.release ?? '')) latest.set(k, e);
  }
  const out = [...latest.values()].filter((e) => {
    if (kind && e.kind !== kind) return false;
    if (scope && e.scope !== scope) return false;
    if (type && typeOf(e) !== type) return false;
    if (!q) return true;
    const reads = [...(e.doc.reads ?? []), ...(e.doc.writes ?? [])].join(' ');
    return [e.name, e.doc.title, e.doc.description, reads, e.doc.id].filter(Boolean).join(' ').toLowerCase().includes(q);
  });
  out.sort((a, b) => a.scope.localeCompare(b.scope) || a.kind.localeCompare(b.kind) || a.name.localeCompare(b.name));
  return { items: out.map(summary), scopes: [...new Set(out.map((e) => e.scope))].sort(), kinds: [...new Set(out.map((e) => e.kind))].sort() };
});

route('GET', '/api/artefacts/:name', (env, req, { name }) => {
  const tenant = tenantOf(req);
  const release = req.query.release;
  const e = req.query.key ? env.store.entries.get(req.query.key) : release ? env.store.get(name, release) : env.store.latest(name) ?? env.store.test(name) ?? env.store.all().find((x) => x.name === name);
  if (!e || (e.tenant !== OWNER_TENANT && e.tenant !== tenant)) throw new HttpError(404, `No artefact ${name} for tenant ${tenant}`);
  const kind = (e.doc?.kind ?? e.kind) as any;
  const doc = e.doc;
  const checker = Checker.fromEnv(env);
  const usedBy = env.store.all().filter((x) => x.status === 'published' && refsOf(x.doc).has(name)).map(summary);
  const tests = env.store.byKind('test').filter((t) => t.doc.target === name).map((t) => ({ id: t.name, title: t.doc.title, level: t.doc.level }));
  const releases = env.store.releasesOf(name, { includeDrafts: true }).map((r) => ({ release: r.release, status: r.status, hash: r.hash, maker: r.maker, checker: r.checker, publishedAt: r.publishedAt }));
  const out: any = {
    entry: summary(e), doc, yaml: toYaml(doc), hash: e.hash, path: e.path, generated: e.generated,
    releases, usedBy, tests, uses: [...refsOf(doc)].sort(),
    issues: checkDocument(env, doc),
    // Read-only to a user the permission table does not let author it (R-11-30).
    readOnly: (e.tenant === OWNER_TENANT && tenant !== OWNER_TENANT) || !!gov.permit(env, req.user!, tenant, 'author', { kind, scope: e.scope }),
  };
  if (e.kind === 'rule' || e.kind === 'table') out.examples = runExamples(doc, (n) => env.store.doc(n));
  if (e.kind === 'table') out.analysis = analyseTable(doc);
  if (e.kind === 'simulator') out.missingStandardScenarios = missingStandardScenarios(doc);
  if (e.kind === 'activity') out.policy = effectivePolicy(doc);
  if (e.kind === 'workflow' || e.kind === 'hook') out.steps = describeSteps(checker, doc);
  if (e.kind === 'scheme') out.dispatch = dispatchTable(doc);
  if (e.kind === 'port') out.simulators = env.store.byKind('simulator').filter((s) => s.doc.port === name).map(summary);
  return out;
});

/** Each step with its effective policy and where each value came from (F-15). */
function describeSteps(checker: Checker, doc: any) {
  return allSteps(doc).map((st) => {
    const act = st.call ? checker.byName.get(st.call) : undefined;
    const pol = act ? effectivePolicy(act, st.policy) : undefined;
    return {
      label: stepLabel(st),
      kind: Object.keys(st).find((k) => ['call', 'switch', 'parallel', 'forEach', 'loop', 'await', 'wait', 'slot', 'use', 'try', 'hook', 'end'].includes(k)),
      call: st.call, type: act?.type, effect: act?.effect,
      outcomes: act?.outcomes?.map((o: any) => o.name),
      handled: Object.keys(st.handle ?? {}),
      policy: pol ? Object.fromEntries(Object.entries(pol).map(([k, v]) => [k, v])) : undefined,
    };
  });
}

function dispatchTable(doc: any) {
  return (doc.messages ?? []).map((m: any) => {
    const d = m.disposition ?? {};
    const kind = Object.keys(d)[0];
    let target = '';
    if (d.start) target = `${d.start.kind}: ${d.start.leg}${d.start.refuseLeg ? `, refuse ${d.start.refuseLeg}` : ''}${d.start.intakeMap ? `, intake ${d.start.intakeMap}` : ''}`;
    else if (d.deliver) target = `correlate by ${d.deliver.correlateBy}`;
    else if (d.handler) target = d.handler;
    else if (d.sentBy) target = d.sentBy;
    else if (d.planned) target = d.planned;
    return { id: m.id, title: m.title, direction: m.direction, isoMessage: m.isoMessage, confirmed: m.confirmed, disposition: kind, target };
  });
}

route('GET', '/api/schemes', (env) => env.store.byKind('scheme').map((e) => ({ scope: e.scope, title: e.doc.title, release: e.release, wire: e.doc.wire, provenance: e.doc.provenance?.status, messages: e.doc.messages?.length, planned: (e.doc.messages ?? []).filter((m: any) => m.disposition?.planned).length })));

route('GET', '/api/schemes/:scope', (env, _req, { scope }) => {
  const s = env.store.scheme(scope);
  if (!s) throw new HttpError(404, `No scheme ${scope}`);
  return { scheme: s, dispatch: dispatchTable(s), yaml: toYaml(s) };
});

// ------------------------------------------------------------ ISO data browser (F-03)

route('GET', '/api/data', (env, req) => {
  const q = (req.query.q ?? '').toLowerCase();
  const paths = new Map<string, { path: string; readers: string[]; writers: string[]; extension?: any }>();
  const touch = (p: string) => {
    if (!paths.has(p)) paths.set(p, { path: p, readers: [], writers: [] });
    return paths.get(p)!;
  };
  const acts = env.store.byKind('activity');
  for (const a of acts) {
    for (const r of a.doc.reads ?? []) touch(r).readers.push(a.name);
    for (const w of a.doc.writes ?? []) touch(w).writers.push(a.name);
  }
  for (const ext of env.store.byKind('extension')) for (const f of ext.doc.fields ?? []) {
    const p = `${f.level === 'item' ? 'items[].' : ''}${ext.doc.root}.${f.path}`;
    touch(p).extension = { ...f, extension: ext.name };
  }
  // A path reads under a broader path: payment.instructedAmount is read by blocks that read payment.
  const all = [...paths.values()];
  for (const p of all) {
    for (const a of acts) {
      if (!p.readers.includes(a.name) && (a.doc.reads ?? []).some((r: string) => r !== p.path && coveredByAny([r], p.path))) p.readers.push(`${a.name} (via ${(a.doc.reads ?? []).find((r: string) => coveredByAny([r], p.path))})`);
      if (!p.writers.includes(a.name) && (a.doc.writes ?? []).some((w: string) => w !== p.path && coveredByAny([w], p.path))) p.writers.push(`${a.name} (via ${(a.doc.writes ?? []).find((w: string) => coveredByAny([w], p.path))})`);
    }
  }
  if (q && !paths.has(req.query.q)) {
    // A path no block names directly, such as payment.instructedAmount.value: show who covers it.
    const p = { path: req.query.q, readers: [] as string[], writers: [] as string[] };
    for (const a of acts) {
      if ((a.doc.reads ?? []).some((r: string) => coveredByAny([r], req.query.q))) p.readers.push(a.name);
      if ((a.doc.writes ?? []).some((w: string) => coveredByAny([w], req.query.q))) p.writers.push(a.name);
    }
    if (p.readers.length || p.writers.length) all.push(p);
  }
  const filtered = all.filter((p) => !q || p.path.toLowerCase().includes(q) || (p as any).extension?.description?.toLowerCase().includes(q));
  filtered.sort((a, b) => a.path.localeCompare(b.path));
  const codes = new Map<string, string[]>();
  for (const r of env.store.byKind('rule')) for (const t of r.doc.then ?? []) if (t.yield?.finding?.reasonCode) {
    const c = t.yield.finding.reasonCode;
    if (!codes.has(c)) codes.set(c, []);
    codes.get(c)!.push(r.name);
  }
  return { parts: CONTEXT_PARTS, paths: filtered, reasonCodes: [...codes].map(([code, rules]) => ({ code, rules })).filter((c) => !q || c.code.toLowerCase().includes(q)) };
});

// ------------------------------------------------------------------ checks

route('POST', '/api/check', (env, req) => {
  const doc = req.body?.doc ?? (req.body?.yaml ? safeParse(req.body.yaml) : undefined);
  if (!doc) throw new HttpError(400, 'Send doc or yaml');
  if (doc.__parseError) return { issues: [{ code: 'L00', severity: 'error', key: '', message: `YAML does not parse: ${doc.__parseError}` }] };
  return { issues: checkDocument(env, doc), interface: interfaceFor(env, doc) };
});

route('GET', '/api/checks', (env) => ({ catalogue: CHECK_CATALOGUE, issues: checkAll(env) }));

function interfaceFor(env: StudioEnv, doc: any) {
  const prev = doc?.name ? env.store.latest(doc.name)?.doc : undefined;
  if (!prev) return undefined;
  const diff = interfaceDiff(prev, doc);
  return { previous: prev.release, ...diff, proposed: diff.required === 'none' ? prev.release : proposeRelease(prev.release, diff.required) };
}

/** An export of a workflow that does not resolve, or for a target with no binding, is a bad request. */
function exportOr400<T>(fn: () => T): T {
  try {
    return fn();
  } catch (e: any) {
    if (/does not resolve|No engine binding/.test(String(e?.message))) throw new HttpError(400, e.message);
    if (e instanceof SignatureRejected) throw new HttpError(409, e.message);
    throw e;
  }
}

function safeParse(y: string): any {
  try {
    const doc = parseYaml(y);
    // An empty body, or YAML that is not a document, is the caller's mistake, not the server's.
    if (doc === null || typeof doc !== 'object' || Array.isArray(doc)) return { __parseError: y.trim() ? 'The YAML is not a document (a mapping)' : 'Send doc or yaml' };
    return doc;
  } catch (e: any) {
    return { __parseError: e.message };
  }
}

// ---------------------------------------------------------- drafts, publish

route('GET', '/api/drafts', (env, req) => {
  const set = gov.draftSet(env, tenantOf(req));
  return env.store.drafts(tenantOf(req)).map((e) => ({ ...summary(e), issues: checkDocument(env, e.doc, tenantOf(req), set).filter((i) => i.severity === 'error').length }));
});

route('POST', '/api/drafts', (env, req) => {
  const tenant = tenantOf(req);
  const doc = req.body.doc ?? safeParse(req.body.yaml ?? '');
  if (doc.__parseError) throw new HttpError(400, `YAML does not parse: ${doc.__parseError}`);
  const actor = actorOf(req);
  enforce(env, actor, tenant, 'author', keyOf(doc), gov.targetOf(doc));
  const e = env.store.saveDraft(tenant, actor, doc);
  // Checked with the tenant's other drafts in place: a designer builds related artefacts together.
  return { draft: summary(e), issues: checkDocument(env, doc, tenant, gov.draftSet(env, tenant)), interface: interfaceFor(env, doc) };
});

route('DELETE', '/api/drafts/:key', (env, req, { key }) => {
  const e = env.store.entries.get(key);
  if (!e || e.tenant !== tenantOf(req)) throw new HttpError(404, `No draft ${key}`);
  must(env, req, 'author', key, gov.targetOf(e.doc));
  env.store.deleteDraft(tenantOf(req), actorOf(req), key);
  return { ok: true };
});

// Approval publishes the change set the studio generates for the draft (R-11-19): the draft and the
// drafts it refers to or that refer to it, checked and tested together, all or none.
route('POST', '/api/publish', async (env, req) => {
  const tenant = tenantOf(req);
  const keys: string[] = Array.isArray(req.body?.keys) ? req.body.keys : req.body?.key ? [req.body.key] : [];
  for (const k of keys) if (env.store.entries.get(k)?.tenant !== tenant) throw new HttpError(404, `No draft ${k}`);
  const r = await gov.approveChangeSet(env, tenant, actorOf(req), keys);
  // The change set is one commit on the content branch, or in pull-request mode one on its own branch and a pull request (R-11-22, R-09-22).
  const git = r.write ? await r.write : undefined;
  return { changeSet: r.changeSet, published: r.published.map(summary), ...(git ? { git: { changeSet: r.changeSet.id, skipped: r.skipped, ...git } } : {}) };
});

route('GET', '/api/change-set', (env, req) => {
  const tenant = tenantOf(req);
  if (!req.query.key) throw new HttpError(400, 'Name the draft: ?key=...');
  if (!env.store.entries.get(req.query.key)) throw new HttpError(404, `No draft ${req.query.key}`);
  const keys = gov.changeSetFor(env, tenant, req.query.key);
  return { key: req.query.key, members: keys.map((k) => summary(env.store.entries.get(k)!)) };
});

route('GET', '/api/change-sets', (env, req) => (env.store.extra.changeSets ?? []).filter((c: any) => c.tenant === tenantOf(req) || (tenantOf(req) === OWNER_TENANT && !req.query.tenant)));

route('POST', '/api/review', (env, req) => {
  submitForReview(env, tenantOf(req), actorOf(req), req.body.key);
  return { ok: true };
});

// A maker asks for the change set a draft is in to become a version (R-11-31). Saving needed no
// approval; this is the request an approver answers.
route('POST', '/api/request-release', (env, req) => {
  const tenant = tenantOf(req);
  const key = req.body?.key;
  const e = key ? env.store.entries.get(key) : undefined;
  if (!e || e.tenant !== tenant) throw new HttpError(404, `No draft ${key}`);
  const keys = gov.changeSetFor(env, tenant, key);
  for (const k of keys) if (env.store.entries.get(k)?.status === 'draft') submitForReview(env, tenant, actorOf(req), k);
  return { requested: keys };
});

route('POST', '/api/new', (env, req) => {
  const { kind, wfKind, from, name, title, source } = req.body ?? {};
  const tenant = tenantOf(req);
  if (from === 'clone') {
    const src = env.store.latest(source, tenant) ?? env.store.latest(source);
    if (!src || (src.tenant !== OWNER_TENANT && src.tenant !== tenant)) throw new HttpError(404, `No ${source} to clone`);
    const doc = JSON.parse(JSON.stringify(src.doc));
    if (name) doc.name = name;
    const parsed = parseName(doc.name);
    doc.release = name ? `${parsed?.major ?? 1}.0.0` : bumpPatch(doc.release);
    doc.title = title ?? doc.title;
    doc.provenance = { origin: tenant === OWNER_TENANT ? 'factory' : 'authored', status: 'provisional', note: `Cloned from ${src.name}@${src.release}` };
    return { doc, yaml: toYaml(doc) };
  }
  const t = templates(kind, wfKind, name, title, from === 'template');
  return { doc: t, yaml: toYaml(t) };
});

function bumpPatch(r: string) {
  const [a, b, c] = r.split('.').map(Number);
  return `${a}.${b}.${c + 1}`;
}

// The audit is read when it is opened, not at start (R-11-27).
route('GET', '/api/audit', async (env, req) => (await env.store.readAudit()).filter((a) => tenantOf(req) === OWNER_TENANT || a.tenant === tenantOf(req)).slice().reverse());

// ---------------------------------------------------------------- testing

let lastRun: TestRunReport[] = [];

route('GET', '/api/tests', (env, req) => {
  const tenant = tenantOf(req);
  return env.store.byKind('test').filter((t) => t.tenant === OWNER_TENANT || t.tenant === tenant).map((t) => {
    const r = lastRun.find((x) => x.id === t.name);
    return { id: t.name, title: t.doc.title, target: t.doc.target, level: t.doc.level, tenant: t.doc.tenant, scope: t.scope, bench: t.doc.bench ?? [], lastResult: r ? (r.passed ? 'pass' : 'fail') : undefined };
  }).sort((a, b) => a.id.localeCompare(b.id));
});

route('GET', '/api/tests/:id', (env, _req, { id }) => {
  const t = env.store.test(id);
  if (!t) throw new HttpError(404, `No test ${id}`);
  return { test: t.doc, yaml: toYaml(t.doc) };
});

route('POST', '/api/tests/:id/run', async (env, req, { id }) => {
  const t = env.store.test(id);
  if (!t || (t.tenant !== OWNER_TENANT && t.tenant !== tenantOf(req))) throw new HttpError(404, `No test ${id}`);
  mayRun(env, req, 'bench', id);
  const test = { ...t.doc, ...(req.body?.override ?? {}) };
  const r = await runTest(env, test, req.body?.config ?? {});
  lastRun = [...lastRun.filter((x) => x.id !== id), r];
  return r;
});

route('POST', '/api/tests/run-all', async (env, req) => {
  const tenant = tenantOf(req);
  mayRun(env, req, 'bench', 'run-all');
  const r = await runAllTests(env, (t) => !t.tenant || t.tenant === tenant || tenant === OWNER_TENANT);
  lastRun = r;
  return r.map((x) => ({ id: x.id, target: x.target, level: x.level, passed: x.passed, outcome: x.outcome, elapsed: x.elapsed, error: x.error?.split('\n')[0], failed: x.checks.filter((c) => !c.passed) }));
});

/** Run an ad hoc bench: any target, any bench, any context (F-16). */
route('POST', '/api/bench/run', async (env, req) => benchRun(env, req, false));
// The debugger (F-17): the same run, with the data after every event and what each step decided.
route('POST', '/api/debug/run', async (env, req) => benchRun(env, req, true));

/** A bench run: any workflow the tenant may see, with the tenant's own configuration (R-11-30). */
async function benchRun(env: StudioEnv, req: Req, debug: boolean) {
  const b = { ...(req.body ?? {}) };
  const tenant = tenantOf(req);
  mayRun(env, req, 'bench', String(b.target ?? 'bench'));
  if (tenant !== OWNER_TENANT) b.tenant = tenant;
  const e = b.target ? env.store.latest(b.target, tenant) ?? env.store.latest(b.target) : undefined;
  const target = e && (e.tenant === OWNER_TENANT || e.tenant === tenant) ? e.doc : undefined;
  if (!target) throw new HttpError(404, `No ${b.target}`);
  const level = b.level ?? (target.kind === 'activity' ? 'activity' : target.kind === 'hook' ? 'hook' : target.wfKind === 'handler' ? 'handler' : target.wfKind === 'leg' ? 'leg' : target.wfKind === 'platform' ? (b.inbound ? 'end-to-end' : 'leg') : 'activity');
  const test = {
    kind: 'test', id: 'BENCH-RUN', title: 'Bench run', target: b.target, level, evaluationDate: b.evaluationDate ?? '2026-11-17',
    tenant: b.tenant && b.tenant !== OWNER_TENANT ? b.tenant : undefined, bench: b.bench ?? [],
    given: { ...(b.context ? { context: b.context } : {}), ...(b.inbound ? { inbound: b.inbound } : {}), ...(b.message ? { message: b.message } : {}), ...(b.signals?.length ? { signals: b.signals } : {}) },
    expect: b.expect ?? {},
  };
  return runTest(env, test, { ...(b.supersessions ? { supersessions: b.supersessions } : {}), ...(debug ? { debug: true } : {}) });
}

route('GET', '/api/simulators', (env, req) => env.store.byKind('simulator').filter((s) => !req.query.port || s.doc.port === req.query.port).map((s) => ({ name: s.name, port: s.doc.port, direction: s.doc.direction, scenarios: (s.doc.scenarios ?? []).map((x: any) => x.name), missingStandard: missingStandardScenarios(s.doc) })));

route('POST', '/api/scripts', (env, req) => authoring.saveScript(env, { tenant: tenantOf(req), actor: actorOf(req), activity: req.body?.activity, language: req.body?.language, source: String(req.body?.source ?? '') }));
route('POST', '/api/samples', (env, req) => authoring.saveSample(env, { tenant: tenantOf(req), actor: actorOf(req), name: req.body?.name, content: req.body?.content }));
route('GET', '/api/samples', (env) => sampleNames(env).map((f) => `samples/${f}`));

import * as nodefs from 'node:fs';

// ------------------------------------------------------- conformance kit

route('GET', '/api/kit', (env) => {
  const k = loadKit(env);
  return { release: k.manifest.release, scenarios: k.manifest.scenarios };
});

route('POST', '/api/kit/run', async (env, req) => runKit(env, req.body?.only ?? []));

// ------------------------------------------------- tenants and configuration

route('GET', '/api/tenants/:tenant/configuration', (env, _req, { tenant }) => {
  const cs = env.store.configurationSet(tenant);
  const checker = Checker.fromEnv(env);
  const params: any[] = [];
  for (const e of env.store.all().filter((x) => x.status === 'published' && x.doc?.parameters)) {
    for (const [k, decl] of Object.entries<any>(e.doc.parameters)) params.push({ parameter: `${e.name}#${k}`, owner: e.name, name: k, ...decl, values: (cs?.values ?? []).filter((v: any) => v.parameter === `${e.name}#${k}`) });
  }
  const hookPoints: any[] = [];
  for (const w of env.store.byKind('workflow')) for (const [n, hp] of Object.entries<any>(checker.hookPoints(w.doc))) {
    if (hp.owner !== w.name) continue;
    hookPoints.push({ hookPoint: `${w.name}#${n}`, ...hp, bindings: (cs?.bindings ?? []).filter((b: any) => b.hookPoint === `${w.name}#${n}`) });
  }
  return { configurationSet: cs, yaml: cs ? toYaml(cs) : '', parameters: params.sort((a, b) => a.parameter.localeCompare(b.parameter)), hookPoints, issues: cs ? checkDocument(env, cs) : [] };
});

route('POST', '/api/tenants/:tenant/configuration/check', (env, req, { tenant }) => {
  const cs = req.body?.doc ?? safeParse(req.body?.yaml ?? '');
  if (cs.__parseError) return { issues: [{ code: 'L00', severity: 'error', message: cs.__parseError }] };
  if (cs.tenant !== tenant) return { issues: [{ code: 'R-01-07', severity: 'error', message: `The configuration set names tenant ${cs.tenant}, not ${tenant}` }] };
  return { issues: checkDocument(env, cs) };
});

/** The compatibility run (F-29, R-06-17): the tenant's hook tests against the current shared releases. */
route('POST', '/api/tenants/:tenant/compatibility', async (env, req, { tenant }) => {
  mayRun(env, req, 'bench', 'compatibility');
  const tests = env.store.byKind('test').filter((t) => t.doc.tenant === tenant);
  const out: any[] = [];
  for (const t of tests) {
    const r = await runTest(env, t.doc);
    out.push({ id: t.name, target: t.doc.target, passed: r.passed, outcome: r.outcome, failed: r.checks.filter((c) => !c.passed) });
  }
  return { tenant, results: out, accepted: out.every((r) => r.passed) };
});

// ----------------------------------------------------------- supersession

route('GET', '/api/supersessions', (env) => env.store.byKind('supersession').map((e) => ({ ...e.doc, issues: checkDocument(env, e.doc) })));

/** Test a supersession (F-25): rerun the pinned workflows' tests with the replacement swapped in. */
route('POST', '/api/supersessions/test', async (env, req) => {
  mayRun(env, req, 'bench', 'supersession');
  const { artefact, replaces, with: by, workflows } = req.body ?? {};
  if (!artefact || !by) throw new HttpError(400, 'Name the artefact and the release that replaces it: { artefact, replaces, with }');
  if (!env.store.get(artefact, by)) throw new HttpError(404, `${artefact}@${by} is not published`);
  const wfs: string[] = workflows ?? [];
  const tests = env.store.byKind('test').filter((t) => wfs.includes(t.doc.target));
  const results: any[] = [];
  for (const t of tests) {
    const r = await runTest(env, t.doc, { supersessions: [{ artefact, replaces, with: by }] });
    results.push({ id: t.name, target: t.doc.target, passed: r.passed, outcome: r.outcome });
  }
  return { artefact, replaces, with: by, results, green: results.length > 0 && results.every((r) => r.passed) };
});

// ------------------------------------------------------------------ export

route('POST', '/api/export', (env, req) => {
  const { workflows, target, withSimulators } = req.body ?? {};
  const tenant = tenantOf(req);
  mayRun(env, req, 'export');
  if (!Array.isArray(workflows) || !workflows.length) throw new HttpError(400, 'Name at least one workflow: { workflows: [...], target }');
  const pkg = exportOr400(() => exportPackage(env, { workflows, target: (target ?? 'temporal') as Target, tenant: tenant && tenant !== OWNER_TENANT ? tenant : undefined, withSimulators: !!withSimulators }));
  return { manifest: pkg.manifest, issues: pkg.issues, files: Object.fromEntries(Object.entries(pkg.files).map(([k, v]) => [k, v.length])), preview: Object.fromEntries(Object.entries(pkg.files).filter(([k]) => k.endsWith('.bpmn20.xml') || k === 'manifest.yaml' || k.startsWith('workflows/') || k === 'portability.json').slice(0, 12)) };
});

route('GET', '/api/export/download', (env, req) => {
  const workflows = (req.query.workflows ?? '').split(',').filter(Boolean);
  if (!workflows.length) throw new HttpError(400, 'Name at least one workflow: ?workflows=a,b&target=...');
  const target = (req.query.target ?? 'temporal') as Target;
  const own = tenantOf(req);
  mayRun(env, req, 'export');
  const tenant = own !== OWNER_TENANT ? own : undefined;
  const pkg = exportOr400(() => exportPackage(env, { workflows, target, tenant, withSimulators: req.query.withSimulators === 'true' }));
  // A download is a release a deployment may take: signed with the studio's key (R-09-23).
  return { __binary: true, data: exportOr400(() => signedZip(pkg)), contentType: 'application/zip', filename: `export-${target}-${workflows.join('+') || 'none'}.zip` };
});

// ------------------------------------------------------- export to a Git branch (W8)

// The connection comes from the environment; what can change is how the studio publishes (R-09-19).
route('GET', '/api/git', (env) => gitExport.gitStatus(env));
route('PUT', '/api/git', (env, req) => ({ settings: gitExport.changeSettings(env, actorOf(req), req.body ?? {}) }));
route('POST', '/api/git/reload', async (env) => {
  await env.store.reload();
  return gitExport.gitStatus(env);
});

// ------------------------------------------------- users and roles (R-11-23)

route('GET', '/api/users/proposals', (env, req) => (tenantOf(req) === OWNER_TENANT ? env.store.extra.userProposals ?? [] : []));
route('POST', '/api/users/proposals', (env, req) => gov.proposeUsers(env, actorOf(req), req.body?.users));
route('POST', '/api/users/proposals/:id/approve', (env, req, { id }) => gov.approveUsers(env, actorOf(req), id));

void keyOf;

// -------------------------------------------------------------- authoring gates

/**
 * The gates a draft passes before it can be published (C16-S1, F-20): schema, static checks,
 * semver, its own examples, and the tests that target it. The same gates apply whoever wrote
 * the draft. The model that drafts is not connected in this build; see docs/ISSUES.md.
 */
route('POST', '/api/gates', async (env, req) => {
  const doc = req.body?.doc ?? safeParse(req.body?.yaml ?? '');
  if (doc.__parseError) return { gates: [{ gate: 'schema', passed: false, detail: doc.__parseError }], passed: false };
  return runGates(env, { doc, script: req.body?.script, tests: req.body?.tests, tenant: tenantOf(req) });
});

// ---------------------------------------------------------------- model and types (C03)

route('GET', '/api/model', (env, req) => {
  const m = modelOf(env.store);
  const q = req.query.q ?? '';
  return { roots: q ? undefined : m.roots, matches: q ? m.search(q) : undefined, personal: m.personalPaths() };
});

route('GET', '/api/types/:name', (env, req, { name }) => {
  const a = env.store.doc(name, undefined, tenantOf(req));
  if (!a || a.kind !== 'activity') throw new HttpError(404, `No activity ${name}`);
  const m = modelOf(env.store);
  return req.query.lang === 'python' ? { language: 'python', file: `${name}.pyi`, source: pythonTypes(m, a) } : { language: 'typescript', file: `${name}.d.ts`, source: typescriptTypes(m, a) };
});

let registry: CodeRegistry | undefined;
const reg = (env: StudioEnv) => (registry ??= CodeRegistry.load(pathmod.join(env.studioRoot, 'registries')));

route('GET', '/api/registry', (env, req) => {
  const r = reg(env);
  const date = req.query.date ?? '2026-11-17';
  const rel = r.at(date);
  return { version: rel.version, provenance: rel.provenance, sets: rel.sets, report: r.reasonCodeReport(env.store.all().map((e) => e.doc), date) };
});

route('GET', '/api/registry/lookup', (env, req) => {
  try {
    return reg(env).lookup(req.query.set, req.query.code, req.query.date ?? '2026-11-17');
  } catch (e) {
    if (e instanceof RegistryError) throw new HttpError(404, e.message);
    throw e;
  }
});

// --------------------------------------------------------------------- expansion (C04-S6)

route('POST', '/api/expanded', (env, req) => {
  const doc = req.body?.doc ?? safeParse(req.body?.yaml ?? '');
  if (doc.__parseError) throw new HttpError(400, `YAML does not parse: ${doc.__parseError}`);
  try {
    const x = expandWorkflow(doc, (n) => env.store.doc(n, undefined, tenantOf(req)));
    return { expanded: x !== doc, doc: x, yaml: toYaml(x) };
  } catch (e: any) {
    throw new HttpError(422, e.message);
  }
});

route('POST', '/api/analyse', (_env, req) => {
  const doc = req.body?.doc ?? safeParse(req.body?.yaml ?? '');
  if (doc.__parseError || doc.kind !== 'table') throw new HttpError(400, 'Send a decision table');
  return analyseTable(doc);
});

route('GET', '/api/expanded/:name', (env, req, { name }) => {
  const d = env.store.doc(name, req.query.release, tenantOf(req));
  if (!d) throw new HttpError(404, `No ${name}`);
  const x = expandWorkflow(d, (n) => env.store.doc(n, undefined, tenantOf(req)));
  return { expanded: x !== d, doc: x, yaml: toYaml(x) };
});

// --------------------------------------------------------------- packs (C05-S2, C19)

route('GET', '/api/packs', (env) => ['p', 'uae.p', 'uae.wps', 'b'].map((scope) => ({ scope, ...compareManifest(env, scope) })));
route('GET', '/api/packs/:scope/manifest', (env, req, { scope }) => {
  const t = tenantOf(req) !== OWNER_TENANT ? tenantOf(req) : undefined;
  return { manifest: packManifest(env, scope, t), compare: compareManifest(env, scope, t) };
});
route('POST', '/api/packs/build', (env, req) => {
  const { scope, kind } = req.body ?? {};
  mayRun(env, req, 'export', 'pack');
  const own = tenantOf(req);
  if (kind === 'bank' && own === OWNER_TENANT) throw new HttpError(403, 'A bank pack is built by its own bank (R-06-01)');
  const tenant = kind === 'bank' ? own : undefined;
  const b = buildPack(env, scope, kind ?? 'shared', tenant);
  return { manifest: b.manifest, refused: b.refused, files: Object.keys(b.files) };
});
route('GET', '/api/packs/download', (env, req) => {
  mayRun(env, req, 'export', 'pack');
  const own = tenantOf(req);
  if (req.query.kind === 'bank' && own === OWNER_TENANT) throw new HttpError(403, 'A bank pack is built by its own bank (R-06-01)');
  const b = buildPack(env, req.query.scope, (req.query.kind ?? 'shared') as any, req.query.kind === 'bank' ? own : undefined);
  if (b.refused) throw new HttpError(422, 'The pack has red checks', { issues: b.refused });
  return { __binary: true, data: packZip(b), contentType: 'application/zip', filename: `pack-${req.query.scope}-${req.query.kind ?? 'shared'}.zip` };
});
route('GET', '/api/packs/:scope/release-gate', async (env, _req, { scope }) => gov.packReleaseGate(env, scope));

// ------------------------------------------------------------ roles and audit (C17)

route('GET', '/api/users', (env, req) => gov.users(env).filter((u) => tenantOf(req) === OWNER_TENANT || u.tenant === tenantOf(req)));
// Ask the permission table about a user of this tenant (R-11-30). It reads; it allows nothing.
route('GET', '/api/permit', (env, req) => {
  const tenant = tenantOf(req);
  const who = gov.userById(env, req.query.user ?? req.user);
  if (!who || (who.tenant !== tenant && tenant !== OWNER_TENANT)) throw new HttpError(404, `No user ${req.query.user} in ${tenant}`);
  const reason = gov.permit(env, who.id, who.tenant, (req.query.action ?? 'author') as Action, { kind: (req.query.kind ?? 'workflow') as any, scope: req.query.scope });
  return { allowed: !reason, reason };
});
route('GET', '/api/permissions', () => ({ roles: ROLES, grants: permissionRows() }));
/** Who is signed in, the roles, and the surfaces the web app shows (R-10-11). */
route('GET', '/api/me', (env, req) => {
  const u = gov.userById(env, req.user)!;
  const roles = effectiveRoles(u.roles);
  return { user: u.id, tenant: u.tenant, roles, surfaces: surfacesOf(roles), landing: landingOf(roles) };
});
route('GET', '/api/audit/verify', async (env) => env.store.verifyAudit());

// --------------------------------------------------- releases, retirement, propagation (C19, C05-S4)

route('GET', '/api/releases/:name/pins', (env, req, { name }) => {
  const release = req.query.release ?? env.store.latest(name)?.release ?? '';
  const pinnedBy = gov.pinnedBy(env, name, release);
  const flying = gov.inFlight(env, name, release);
  return { name, release, pinnedBy, inFlight: flying, mayRetire: pinnedBy.length === 0 && flying.count === 0 };
});
route('POST', '/api/retire', (env, req) => ({ retired: summary(gov.retire(env, actorOf(req), req.body.name, req.body.release)) }));
route('POST', '/api/in-flight', (env, req) => {
  gov.importInFlight(env, actorOf(req), req.body.counts ?? {}, req.body.asOf);
  return { ok: true };
});
route('GET', '/api/propagate', async (env, req) => {
  if (!req.query.name || !req.query.release) throw new HttpError(400, 'Name the artefact and its new release: ?name=...&release=...');
  return gov.propagate(env, req.query.name, req.query.release);
});

// ------------------------------------------------------------ supersession (C19-S4, S5)

route('POST', '/api/supersessions/draft', (env, req) => gov.draftSupersession(env, { ...req.body, maker: actorOf(req) }));
const supersessionDoc = (req: Req) => {
  const doc = req.body?.doc;
  if (!doc || doc.kind !== 'supersession') throw new HttpError(400, 'Send the supersession document as doc');
  return doc;
};
route('POST', '/api/supersessions/evidence', async (env, req) => {
  mayRun(env, req, 'bench', 'supersession');
  return gov.testSupersession(env, supersessionDoc(req));
});
route('POST', '/api/supersessions/hand-over', (env, req) => gov.handOverSupersession(env, supersessionDoc(req), actorOf(req)));
route('GET', '/api/supersessions/handed-over', (env) => env.store.extra.supersessions ?? []);

// ------------------------------------------------- tenants and configuration (C18)

route('GET', '/api/tenants/:tenant/settable', (env, _req, { tenant }) => gov.settableParameters(env, tenant));
// A bank configurator's dials and hook bindings: saved as a draft with no approval; a bank approver
// who is not its maker makes it the version the runtime is given, through /api/publish (R-11-31).
route('POST', '/api/tenants/:tenant/configuration', (env, req, { tenant }) => {
  const d = summary(gov.saveConfiguration(env, tenant, actorOf(req), req.body.yaml));
  return { draft: d, saved: d };
});
route('POST', '/api/tenants/:tenant/configuration/validate', (env, req, { tenant }) => {
  const doc = req.body?.doc ?? safeParse(req.body?.yaml ?? '');
  if (doc.__parseError) return { issues: [{ code: 'L00', severity: 'error', message: doc.__parseError }] };
  return { issues: gov.checkConfiguration(env, tenant, doc) };
});
route('POST', '/api/tenants/:tenant/compatibility-run', async (env, req, { tenant }) => mayRun(env, req, 'bench', 'compatibility') ?? gov.compatibilityRun(env, tenant, (req.body?.candidates ?? []).map((c: any) => (typeof c === 'string' ? safeParse(c) : c))));
route('GET', '/api/tenants/:tenant/export', (env, _req, { tenant }) => gov.tenantExport(env, tenant));

// ------------------------------------------------------ conformance report (C23-S3)

route('GET', '/api/conformance/report', async (env, req) => gov.conformanceReport(env, req.query.target ?? 'studio'));
route('POST', '/api/conformance/waivers', (env, req) => {
  const actor = actorOf(req);
  return { ok: true, ...gov.addWaiver(env, actor, { target: req.body.target ?? 'studio', scenario: req.body.scenario, reason: req.body.reason, approver: actor }) };
});

// ------------------------------------------------------------------ simulators (C10)

route('GET', '/api/simulators/:name/stub', (env, _req, { name }) => {
  const sim = env.store.doc(name);
  if (!sim || sim.kind !== 'simulator') throw new HttpError(404, `No simulator ${name}`);
  return { __binary: true, data: Buffer.from(stubSource(sim), 'utf8'), contentType: 'text/javascript', filename: `${name}.stub.cjs` };
});
route('GET', '/api/coverage/:scope', (env, _req, { scope }) => reasonCodeCoverage(env, scope));

// ------------------------------------------------------------------ tests (C11-S5)

route('GET', '/api/test-coverage', (env) => testCoverageReport(env));
route('GET', '/api/test-coverage/:name', (env, _req, { name }) => untestedOutcomes(env, name));

// ----------------------------------------------------------------- samples (F-05)

route('GET', '/api/sample-messages', () => messagesWithSamples());
route('GET', '/api/sample-messages/:message', async (env, req, { message }) => samplesFor(env, message, req.query.prove !== 'false'));

// ----------------------------------------------------------------- bindings (C20-S1)

route('GET', '/api/bindings', (env) => compareBindings(env));

// --------------------------------------------------------------------- AI (C16)

route('GET', '/api/ai/status', () => (process.env.TH_AI_REPLAY ? { configured: true, how: 'replay' } : assistantConfigured()));
route('GET', '/api/ai/templates', (_env, req) => TEMPLATES.filter((t) => !req.query.kind || t.kind === req.query.kind));
// The inline assistant beside every surface (I-29). It needs credentials, or, for tests and offline
// demonstrations, a recorded conversation in TH_AI_REPLAY (a JSON list of model turns), which it
// labels as a replay.
route('POST', '/api/ai/agent', async (env, req) => {
  const body = req.body ?? {};
  const actor = actorOf(req);
  if (body.context?.actor && body.context.actor !== actor) throw new HttpError(403, `The assistant acts as the signed-in user, ${actor} (R-11-32)`);
  const ctx = { ...(body.context ?? {}), tenant: tenantOf(req), actor };
  if (!ctx.surface) throw new HttpError(400, 'Send context.surface');
  mayRun(env, req, 'assistant');
  const replay = process.env.TH_AI_REPLAY;
  let model: AgentModel;
  if (replay) model = replayModel(JSON.parse(nodefs.readFileSync(replay, 'utf8')));
  else {
    const st = assistantConfigured();
    if (!st.configured) throw new HttpError(503, `The assistant has no model credentials. ${st.how}`);
    model = anthropicAgentModel();
  }
  // The assistant's actions go through the same API as the user, so the same table decides (R-11-30).
  const inner = (method: string, path: string, b: any = {}) => dispatch(env, { method, path, query: { tenant: ctx.tenant }, body: { tenant: ctx.tenant, ...b }, user: actor });
  return agentTurn(env, { ...body, context: ctx }, model, inner);
});

// Import (I-33): an extraction bundle, as a zip in base64, previewed and then saved as drafts. The
// import never publishes. The training bundle and the extraction prompt are served for download.
const bundleOf = (body: any) => {
  if (!body?.bundle) throw new HttpError(400, 'Send bundle: the extraction bundle zip, in base64');
  return readBundle(Buffer.from(String(body.bundle), 'base64'));
};
// Import (R-11-30): a platform user imports the shared part of a bundle, a bank configurator the bank
// part, into its own tenant. Neither names the other tenant; the other part is reported, not saved.
function importOpts(req: Req) {
  const tenant = tenantOf(req);
  if (req.body?.bankActor && req.body.bankActor !== req.user) throw new HttpError(403, `The bank part is saved as the signed-in user, ${req.user}, not ${req.body.bankActor} (R-11-32)`);
  if (tenant === OWNER_TENANT) {
    if (req.body?.bankTenant) throw new HttpError(403, `A platform user imports the shared part only; a configurator of ${req.body.bankTenant} imports the bank part (R-06-01)`);
    return { tenant: OWNER_TENANT, part: 'shared' as const };
  }
  if (req.body?.bankTenant && req.body.bankTenant !== tenant) throw new HttpError(403, `${req.user} works for tenant ${tenant}, not ${req.body.bankTenant} (R-06-01)`);
  return { tenant: OWNER_TENANT, bankTenant: tenant, part: 'bank' as const };
}
route('POST', '/api/import/preview', async (env, req) => {
  mayRun(env, req, 'import');
  return analyseImport(env, bundleOf(req.body), { ...importOpts(req), runTests: req.body?.runTests !== false });
});
route('POST', '/api/import/apply', async (env, req) => {
  mayRun(env, req, 'import');
  const actor = actorOf(req);
  return applyImport(env, bundleOf(req.body), { ...importOpts(req), actor, bankActor: actor, force: !!req.body?.force });
});
route('GET', '/api/import/sample', (env) => ({ __binary: true, data: bundleZip(wpsTrainingBundle(env)), contentType: 'application/zip', filename: 'extraction-uae.wpsx-training.zip' }));
route('GET', '/api/import/prompt', (env) => {
  const f = pathmod.join(env.studioRoot, '..', 'docs', 'import', 'EXTRACTION-PROMPT.md');
  if (!nodefs.existsSync(f)) throw new HttpError(404, 'docs/import/EXTRACTION-PROMPT.md is not here');
  return { __binary: true, data: nodefs.readFileSync(f), contentType: 'text/markdown; charset=utf-8', filename: 'EXTRACTION-PROMPT.md' };
});

// "What is happening": plain words, a checklist and the next step, with no model (I-29).
route('GET', '/api/help/fields', (_env, req) => (req.query.label ? fieldHelp(req.query.label) ?? null : FIELD_HELP));
route('POST', '/api/explain', async (env, req) => {
  const { surface, doc, selected, report } = req.body ?? {};
  const tenant = tenantOf(req);
  if (surface === 'bench') return explainBench(env, doc, report);
  if (doc?.kind === 'configuration-set') return explainDoc(env, doc, { issues: gov.checkConfiguration(env, tenant, doc) });
  const gates = doc?.kind ? (await runGates(env, { doc, tenant })).gates : undefined;
  return explainDoc(env, doc, { gates, selected });
});

route('POST', '/api/ai/draft', async (env, req) => {
  mayRun(env, req, 'assistant');
  const st = assistantConfigured();
  if (!st.configured) throw new HttpError(503, `The assistant has no model credentials. ${st.how}`);
  return draft(env, { ...req.body, tenant: tenantOf(req) });
});
