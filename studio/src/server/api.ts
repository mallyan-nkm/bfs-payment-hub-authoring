import { parseYaml, toYaml } from '../core/yaml.js';
import { OWNER_TENANT, StoreError, keyOf } from '../core/store.js';
import { parseName } from '../core/names.js';
import { CONTEXT_PARTS, coveredByAny } from '../core/paths.js';
import { Checker, CHECK_CATALOGUE, checkAll, checkDocument, interfaceDiff, proposeRelease, refsOf, allSteps, stepLabel } from '../checks/checks.js';
import { analyseTable, runExamples } from '../rules/service.js';
import { effectivePolicy } from '../interp/policy.js';
import { missingStandardScenarios } from '../sim/runtime.js';
import { runTest, runAllTests, type TestRunReport } from '../testing/runner.js';
import { runKit, loadKit } from '../conformance/kit.js';
import { exportPackage, exportZip, type Target } from '../export/exporter.js';
import { templates } from './templates.js';
import type { StudioEnv } from '../env.js';

// The studio's HTTP API. Every call names the tenant it acts for; a tenant sees shared content
// and its own, and nothing of another tenant (R-06-01, R-10-05). The studio never touches a
// running payment (R-10-01): it has no connection to one.

export class HttpError extends Error {
  constructor(public status: number, message: string, public body?: any) {
    super(message);
  }
}

export interface Req {
  method: string;
  path: string;
  query: Record<string, string>;
  body: any;
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
    try {
      const body = await r.handler(env, req, params);
      if (body && body.__binary) return { status: 200, body: body.data, contentType: body.contentType, filename: body.filename };
      return { status: 200, body };
    } catch (e: any) {
      if (e instanceof HttpError) return { status: e.status, body: { error: e.message, ...(e.body ?? {}) } };
      if (e instanceof StoreError) return { status: 409, body: { error: e.message, code: e.code } };
      return { status: 500, body: { error: String(e?.message ?? e) } };
    }
  }
  return { status: 404, body: { error: `No route ${req.method} ${req.path}` } };
}

const tenantOf = (req: Req) => req.query.tenant || req.body?.tenant || OWNER_TENANT;

function visibleEntries(env: StudioEnv, tenant: string) {
  return env.store.all().filter((e) => e.tenant === OWNER_TENANT || e.tenant === tenant);
}

function typeOf(e: any): string {
  return e.doc?.type ?? e.doc?.wfKind ?? e.doc?.mode ?? e.doc?.hitPolicy ?? e.doc?.direction ?? '';
}

function summary(e: any) {
  return {
    key: e.key, name: e.name, kind: e.kind, type: typeOf(e), scope: e.scope, release: e.release, title: e.doc?.title ?? e.doc?.id ?? e.name,
    status: e.status, tenant: e.tenant, provenance: e.doc?.provenance?.status, generated: e.generated, source: e.source,
  };
}

// ------------------------------------------------------------------- meta

route('GET', '/api/meta', (env) => {
  const counts: Record<string, number> = {};
  for (const e of env.store.all()) counts[e.kind] = (counts[e.kind] ?? 0) + 1;
  return {
    tenants: env.store.tenants(),
    owner: OWNER_TENANT,
    counts,
    checks: CHECK_CATALOGUE,
    contextParts: CONTEXT_PARTS,
    roles: ['shared designer', 'shared checker', 'bank designer', 'bank checker', 'auditor'],
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
  const doc = e.doc;
  const checker = Checker.fromEnv(env);
  const usedBy = env.store.all().filter((x) => x.status === 'published' && refsOf(x.doc).has(name)).map(summary);
  const tests = env.store.byKind('test').filter((t) => t.doc.target === name).map((t) => ({ id: t.name, title: t.doc.title, level: t.doc.level }));
  const releases = env.store.releasesOf(name, { includeDrafts: true }).map((r) => ({ release: r.release, status: r.status, hash: r.hash, maker: r.maker, checker: r.checker, publishedAt: r.publishedAt }));
  const out: any = {
    entry: summary(e), doc, yaml: toYaml(doc), hash: e.hash, path: e.path, generated: e.generated,
    releases, usedBy, tests, uses: [...refsOf(doc)].sort(),
    issues: checkDocument(env, doc),
    readOnly: e.tenant === OWNER_TENANT && tenant !== OWNER_TENANT,
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

function safeParse(y: string): any {
  try {
    return parseYaml(y);
  } catch (e: any) {
    return { __parseError: e.message };
  }
}

// ---------------------------------------------------------- drafts, publish

route('GET', '/api/drafts', (env, req) => env.store.drafts(tenantOf(req)).map((e) => ({ ...summary(e), issues: checkDocument(env, e.doc).filter((i) => i.severity === 'error').length })));

route('POST', '/api/drafts', (env, req) => {
  const tenant = tenantOf(req);
  const doc = req.body.doc ?? safeParse(req.body.yaml ?? '');
  if (doc.__parseError) throw new HttpError(400, `YAML does not parse: ${doc.__parseError}`);
  const e = env.store.saveDraft(tenant, req.body.actor ?? 'designer', doc);
  return { draft: summary(e), issues: checkDocument(env, doc), interface: interfaceFor(env, doc) };
});

route('DELETE', '/api/drafts/:key', (env, req, { key }) => {
  env.store.deleteDraft(tenantOf(req), req.query.actor ?? 'designer', key);
  return { ok: true };
});

route('POST', '/api/publish', (env, req) => {
  const tenant = tenantOf(req);
  const d = env.store.entries.get(req.body.key);
  if (!d) throw new HttpError(404, `No draft ${req.body.key}`);
  const errors = checkDocument(env, d.doc).filter((i) => i.severity === 'error');
  if (errors.length) throw new HttpError(422, 'The draft has errors; fix them before publish', { issues: errors });
  if (d.kind === 'scheme' && (d.doc.messages ?? []).some((m: any) => m.disposition?.planned)) throw new HttpError(422, 'A pack with a planned message cannot publish (R-05-22)');
  const e = env.store.publish(tenant, req.body.key, req.body.maker ?? d.maker ?? 'designer', req.body.checker);
  return { published: summary(e) };
});

route('POST', '/api/new', (env, req) => {
  const { kind, wfKind, from, name, title, source } = req.body ?? {};
  const tenant = tenantOf(req);
  if (from === 'clone') {
    const src = env.store.latest(source);
    if (!src) throw new HttpError(404, `No ${source} to clone`);
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

route('GET', '/api/audit', (env, req) => env.store.audit.filter((a) => tenantOf(req) === OWNER_TENANT || a.tenant === tenantOf(req)).slice().reverse());

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
  if (!t) throw new HttpError(404, `No test ${id}`);
  const test = { ...t.doc, ...(req.body?.override ?? {}) };
  const r = await runTest(env, test, req.body?.config ?? {});
  lastRun = [...lastRun.filter((x) => x.id !== id), r];
  return r;
});

route('POST', '/api/tests/run-all', async (env, req) => {
  const tenant = tenantOf(req);
  const r = await runAllTests(env, (t) => !t.tenant || t.tenant === tenant || tenant === OWNER_TENANT);
  lastRun = r;
  return r.map((x) => ({ id: x.id, target: x.target, level: x.level, passed: x.passed, outcome: x.outcome, elapsed: x.elapsed, error: x.error?.split('\n')[0], failed: x.checks.filter((c) => !c.passed) }));
});

/** Run an ad hoc bench: any target, any bench, any context (F-16). */
route('POST', '/api/bench/run', async (env, req) => {
  const b = req.body ?? {};
  const target = env.store.latest(b.target)?.doc;
  if (!target) throw new HttpError(404, `No ${b.target}`);
  const level = b.level ?? (target.kind === 'activity' ? 'activity' : target.kind === 'hook' ? 'hook' : target.wfKind === 'handler' ? 'handler' : target.wfKind === 'leg' ? 'leg' : target.wfKind === 'platform' ? (b.inbound ? 'end-to-end' : 'leg') : 'activity');
  const test = {
    kind: 'test', id: 'BENCH-RUN', title: 'Bench run', target: b.target, level, evaluationDate: b.evaluationDate ?? '2026-11-17',
    tenant: b.tenant && b.tenant !== OWNER_TENANT ? b.tenant : undefined, bench: b.bench ?? [],
    given: { ...(b.context ? { context: b.context } : {}), ...(b.inbound ? { inbound: b.inbound } : {}), ...(b.signals?.length ? { signals: b.signals } : {}) },
    expect: b.expect ?? {},
  };
  return runTest(env, test, b.supersessions ? { supersessions: b.supersessions } : {});
});

route('GET', '/api/simulators', (env, req) => env.store.byKind('simulator').filter((s) => !req.query.port || s.doc.port === req.query.port).map((s) => ({ name: s.name, port: s.doc.port, direction: s.doc.direction, scenarios: (s.doc.scenarios ?? []).map((x: any) => x.name), missingStandard: missingStandardScenarios(s.doc) })));

route('GET', '/api/samples', (env) => {
  const fs = require_fs();
  const out: string[] = [];
  for (const d of env.samplesDirs) if (fs.existsSync(d)) out.push(...fs.readdirSync(d).filter((f: string) => !f.endsWith('.md')).map((f: string) => `samples/${f}`));
  return out.sort();
});

import * as nodefs from 'node:fs';
function require_fs() {
  return nodefs;
}

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
route('POST', '/api/tenants/:tenant/compatibility', async (env, _req, { tenant }) => {
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
  const { artefact, replaces, with: by, workflows } = req.body ?? {};
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
  const { workflows, target, tenant } = req.body ?? {};
  const pkg = exportPackage(env, { workflows, target: (target ?? 'temporal') as Target, tenant: tenant && tenant !== OWNER_TENANT ? tenant : undefined });
  return { manifest: pkg.manifest, issues: pkg.issues, files: Object.fromEntries(Object.entries(pkg.files).map(([k, v]) => [k, v.length])), preview: Object.fromEntries(Object.entries(pkg.files).filter(([k]) => k.endsWith('.bpmn20.xml') || k === 'manifest.yaml' || k.startsWith('workflows/') || k === 'portability.json').slice(0, 12)) };
});

route('GET', '/api/export/download', (env, req) => {
  const workflows = (req.query.workflows ?? '').split(',').filter(Boolean);
  const target = (req.query.target ?? 'temporal') as Target;
  const tenant = req.query.tenant && req.query.tenant !== OWNER_TENANT ? req.query.tenant : undefined;
  const pkg = exportPackage(env, { workflows, target, tenant });
  return { __binary: true, data: exportZip(pkg), contentType: 'application/zip', filename: `export-${target}-${workflows.join('+') || 'none'}.zip` };
});

void keyOf;

// -------------------------------------------------------------- authoring gates

/**
 * The gates a draft passes before it can be published (C16-S1, F-20): schema, static checks,
 * semver, its own examples, and the tests that target it. The same gates apply whoever wrote
 * the draft. The model that drafts is not connected in this build; see docs/ISSUES.md.
 */
route('POST', '/api/gates', async (env, req) => {
  const doc = req.body?.doc ?? safeParse(req.body?.yaml ?? '');
  if (doc.__parseError) return { gates: [{ gate: 'parse', passed: false, detail: doc.__parseError }], passed: false };
  const gates: { gate: string; passed: boolean; detail?: any }[] = [];
  const issues = checkDocument(env, doc);
  const schema = issues.filter((i) => i.code === 'L00');
  gates.push({ gate: 'schema', passed: schema.length === 0, detail: schema });
  const statics = issues.filter((i) => i.code !== 'L00' && i.code !== 'L26' && i.severity === 'error');
  gates.push({ gate: 'static checks', passed: statics.length === 0, detail: statics });
  const semver = issues.filter((i) => i.code === 'L26');
  gates.push({ gate: 'semver', passed: semver.length === 0, detail: semver });
  if (doc.kind === 'rule' || doc.kind === 'table') {
    const ex = runExamples(doc, (n) => env.store.doc(n));
    const hasPassFail = doc.kind === 'table' || (ex.some((e) => (e.expected.findings ?? []).length === 0) && ex.some((e) => (e.expected.findings ?? []).length > 0));
    gates.push({ gate: 'examples', passed: ex.length > 0 && ex.every((e) => e.passed) && hasPassFail, detail: ex });
  }
  if (doc.kind === 'simulator') {
    const missing = missingStandardScenarios(doc);
    gates.push({ gate: 'standard scenarios', passed: missing.length === 0, detail: missing });
  }
  const tests = env.store.byKind('test').filter((t) => t.doc.target === doc.name);
  if (doc.kind === 'activity') {
    // R-03-16: at least one test per outcome. A test names the outcome it expects.
    const covered = new Set(tests.map((t) => t.doc.expect?.outcome));
    const uncovered = (doc.outcomes ?? []).map((o: any) => o.name).filter((o: string) => !covered.has(o));
    gates.push({ gate: 'a test per outcome', passed: uncovered.length === 0, detail: uncovered.length ? `No test expects: ${uncovered.join(', ')}` : undefined });
  }
  if (tests.length) {
    const results: any[] = [];
    for (const t of tests) {
      const r = await runTest(env, t.doc, { documents: { [doc.name]: doc, [`${doc.name}@${doc.release}`]: doc } });
      results.push({ id: t.name, passed: r.passed, failed: r.checks.filter((c) => !c.passed) });
    }
    gates.push({ gate: 'tests', passed: results.every((r) => r.passed), detail: results });
  }
  gates.push({ gate: 'provenance', passed: !!doc.provenance?.origin, detail: doc.provenance ?? 'No provenance: say where the draft came from (F-21)' });
  return { gates, passed: gates.every((g) => g.passed) };
});
