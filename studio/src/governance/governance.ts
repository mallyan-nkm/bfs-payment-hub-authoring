import { OWNER_TENANT, StoreError, identifiers, mentions, type Entry } from '../core/store.js';
import { USERS_PATH, handedOverPath, inFlightPath } from '../content/layout.js';
import { commitMessage, filesFor } from '../export/git.js';
import { buildPlanned, planOnApproval, type Plan } from './distributions.js';
import { compareReleases, isShared, parseName } from '../core/names.js';
import { toYaml, parseYaml } from '../core/yaml.js';
import { Checker, checkDocument, interfaceDiff, pinnable, proposeRelease, refsOf } from '../checks/checks.js';
import { runTest } from '../testing/runner.js';
import { runKit } from '../conformance/kit.js';
import { exportPackage, packageHash } from '../export/exporter.js';
import { SignatureRejected, signingKey } from '../export/signing.js';
import { buildPack } from './packs.js';
import { ROLES, ROLE_TENANT, effectiveRoles, granted, kindOf, scopeClass, type Action, type Kind } from './permissions.js';
import type { StudioEnv } from '../env.js';

// Governance (Part 6, Part 11; C17, C18, C19, C05-S4, C23-S3).

// ------------------------------------------------------------------ roles (C17-S2, R-11-29)

export type { Role } from './permissions.js';

/** A user of the studio. `subject` is the identity provider's claim that signs in as this user, when it is not the id. */
export interface User { id: string; tenant: string; roles: string[]; subject?: string }

/** The studio's users. Names are role-shaped placeholders: no real person is named. */
export const DEFAULT_USERS: User[] = [
  { id: 'shared.designer', tenant: OWNER_TENANT, roles: ['workflow designer', 'workflow developer', 'operations', 'service engineer'] },
  { id: 'workflow.designer', tenant: OWNER_TENANT, roles: ['workflow designer'] },
  { id: 'workflow.developer', tenant: OWNER_TENANT, roles: ['workflow developer'] },
  { id: 'operations.designer', tenant: OWNER_TENANT, roles: ['operations'] },
  { id: 'service.engineer', tenant: OWNER_TENANT, roles: ['service engineer'] },
  { id: 'shared.checker', tenant: OWNER_TENANT, roles: ['platform approver'] },
  { id: 'shared.auditor', tenant: OWNER_TENANT, roles: ['auditor'] },
  { id: 'service.operations', tenant: OWNER_TENANT, roles: ['service operations'] },
  { id: 'users.admin', tenant: OWNER_TENANT, roles: ['administrator'] },
  { id: 'users.approver', tenant: OWNER_TENANT, roles: ['administrator'] },
  { id: 'example.maker', tenant: 'example-bank', roles: ['bank configurator'] },
  { id: 'example.checker', tenant: 'example-bank', roles: ['bank approver'] },
  { id: 'example.auditor', tenant: 'example-bank', roles: ['auditor'] },
  { id: 'second.maker', tenant: 'second-bank', roles: ['bank configurator'] },
  { id: 'second.checker', tenant: 'second-bank', roles: ['bank approver'] },
];

export type { Action, Kind } from './permissions.js';

export function users(env: StudioEnv): User[] {
  return env.store.extra.users ?? DEFAULT_USERS;
}

/**
 * The roles the identity provider asserts for the signed-in user, when the studio maps groups
 * (R-11-34): the call runs inside `withAssertedRoles`, and the user holds only the roles of
 * governance/users.yaml that are also asserted. Outside a call, nothing is narrowed.
 */
const asserted = new AsyncLocalStorage<{ user: string; roles: string[] }>();
export function withAssertedRoles<T>(user: string, roles: string[] | undefined, fn: () => T): T {
  return roles ? asserted.run({ user, roles }, fn) : fn();
}

export function userById(env: StudioEnv, id: string | undefined): User | undefined {
  const u = id ? users(env).find((x) => x.id === id) : undefined;
  const a = asserted.getStore();
  if (!u || !a || a.user !== u.id) return u;
  return { ...u, roles: effectiveRoles(u.roles).filter((r) => a.roles.includes(r)) };
}

/** What a permission is asked about: the kind and, for an artefact, its scope. */
export interface Target { kind: Kind; scope?: string }

/**
 * Is this actor allowed this action in this tenant on this kind in this scope? Returns the reason
 * when not. The answer comes from the one permission table (permissions.ts, R-11-30): the user must
 * be a user of the studio, act in its own tenant, and hold a role whose row grants the action on the
 * kind in the scope class. A bank works only in scope b; the owner only in shared scopes.
 */
export function permit(env: StudioEnv, actor: string, tenant: string, action: Action, target: Target): string | undefined {
  const u = userById(env, actor);
  if (!u) return `${actor || 'nobody'} is not a user of the studio`;
  if (u.tenant !== tenant) return `${actor} belongs to tenant ${u.tenant}, not ${tenant} (R-06-01)`;
  if (action === 'read') return undefined;
  const roles = effectiveRoles(u.roles).filter((r) => ROLE_TENANT[r] === 'either' || (ROLE_TENANT[r] === 'bank') === (tenant !== OWNER_TENANT));
  const cls = scopeClass(target.scope ?? (tenant === OWNER_TENANT ? 'p' : 'b'), target.kind);
  if (cls !== 'studio') {
    if (tenant !== OWNER_TENANT && cls === 'shared') return `${actor} works for bank ${tenant}, only in scope b; ${target.scope} is shared content (R-06-03)`;
    if (tenant === OWNER_TENANT && cls === 'bank') return 'The owner tenant does not author bank content (R-06-03)';
  }
  if (granted(roles, cls, target.kind, action).length) return undefined;
  const held = roles.length ? roles.join(', ') : 'no role';
  return `${actor} (${held}) may not ${action} a ${target.kind}${cls === 'studio' ? '' : ` in ${cls === 'bank' ? 'scope b' : `scope ${target.scope ?? 'shared'}`}`} (R-11-30)`;
}

/** An authorisation refusal: the API answers 403. */
export class Forbidden extends StoreError {
  constructor(message: string) {
    super('R-06', message);
  }
}

export function enforce(env: StudioEnv, actor: string, tenant: string, action: Action, key: string, target: Target) {
  const why = permit(env, actor, tenant, action, target);
  if (why) {
    env.store.record({ tenant, actor, action, key, outcome: 'refused', detail: `${target.kind}: ${why}` });
    throw new Forbidden(why);
  }
}

/** The kind and scope of a document, as the permission table asks for them. */
export function targetOf(doc: any): Target {
  const kind = kindOf(doc);
  if (!kind) throw new StoreError('L00', `${doc?.kind ?? 'A document without a kind'} is not a kind the studio authors`);
  return { kind, scope: doc.kind === 'configuration-set' ? 'b' : doc.name ? parseName(doc.name)?.scope : doc.scope };
}

// --------------------------------------------------------- release flow (C19-S2)

/** The tenant's drafts, as the working set a draft is checked with while it is edited. */
export function draftSet(env: StudioEnv, tenant: string): any[] {
  return env.store.drafts(tenant).map((d) => d.doc);
}

export function submitForReview(env: StudioEnv, tenant: string, actor: string, key: string) {
  const e = env.store.entries.get(key);
  if (!e || e.status !== 'draft' || e.tenant !== tenant) throw new StoreError('not-found', `No draft ${key}`);
  enforce(env, actor, tenant, 'author', key, targetOf(e.doc));
  const errors = checkDocument(env, e.doc, tenant, draftSet(env, tenant)).filter((i) => i.severity === 'error');
  if (errors.length) throw new StoreError('checks', `The draft has ${errors.length} error(s): ${errors.map((x) => x.code).join(', ')}`);
  env.store.setState(key, 'in-review');
  env.store.record({ tenant, actor, action: 'submit-for-review', key });
}

/** Approve one draft: the change set the studio generates for it is published with it (R-11-19). */
export async function approve(env: StudioEnv, tenant: string, checker: string, key: string) {
  const name = env.store.entries.get(key)?.name;
  const r = await approveChangeSet(env, tenant, checker, [key]);
  return r.published.find((e) => e.name === name)!;
}

/**
 * The change set the studio generates for a draft (R-11-19): the draft and every draft of the
 * tenant that refers to it or that it refers to, directly or through others. Drafts that refer to
 * one another (a port and its simulator, a composite and its subflow, a scheme and its legs) can
 * only publish together.
 */
export function changeSetFor(env: StudioEnv, tenant: string, key: string): string[] {
  const drafts = env.store.drafts(tenant);
  const start = drafts.find((d) => d.key === key);
  if (!start) throw new StoreError('not-found', `No draft ${key} in ${tenant}`);
  const set = new Set<string>([key]);
  const queue = [start];
  while (queue.length) {
    const m = queue.shift()!;
    const mine = identifiers(m.doc);
    for (const x of drafts) {
      if (set.has(x.key)) continue;
      if (mentions(m.doc, identifiers(x.doc)) || mentions(x.doc, mine)) {
        set.add(x.key);
        queue.push(x);
      }
    }
  }
  return [...set].sort();
}

/**
 * Approve and publish a change set (R-11-19). The studio generates it from the drafts named: each
 * member is checked with the others in place, the tests that target any member are green with the
 * whole set in place, the checker has the checker role and is not the maker of any member, and
 * either all publish or none does. The set is recorded, so that an export can list it (R-11-20).
 */
export async function approveChangeSet(env: StudioEnv, tenant: string, checker: string, requested: string[]) {
  if (!Array.isArray(requested) || requested.length === 0) throw new StoreError('not-found', 'Name the draft to approve');
  const keys = [...new Set(requested.flatMap((k) => changeSetFor(env, tenant, k)))].sort();
  const set = keys.map((key) => {
    const e = env.store.entries.get(key)!;
    if (e.tenant !== tenant || (e.status !== 'in-review' && e.status !== 'draft')) throw new StoreError('not-found', `No draft in review ${key}`);
    if (e.awaitingMerge) throw new StoreError('R-09-22', `${key} is approved; its pull request from ${e.awaitingMerge} is waiting to merge`);
    enforce(env, checker, tenant, 'approve', key, targetOf(e.doc));
    const maker = env.store.audit.filter((a) => a.key === key && a.action === 'save-draft').pop()?.actor ?? e.maker ?? '';
    if (!maker || maker === checker) {
      env.store.record({ tenant, actor: checker, action: 'publish', key, outcome: 'refused', detail: 'Maker and checker must differ (R-06-04)' });
      throw new StoreError('R-06-04', `Maker and checker must differ (${key})`);
    }
    if (e.kind === 'scheme' && (e.doc.messages ?? []).some((m: any) => m.disposition?.planned)) throw new StoreError('R-05-22', 'A pack with a planned message cannot publish');
    return { key, e, maker };
  });
  const docs = set.map((x) => x.e.doc);
  const problems = set.flatMap(({ e }) => checkDocument(env, e.doc, tenant, docs).filter((i) => i.severity === 'error').map((i) => `${e.name}: ${i.code} ${i.message}`));
  if (problems.length) {
    env.store.record({ tenant, actor: checker, action: 'publish', key: keys.join(','), outcome: 'refused', detail: problems.join('; ') });
    throw new StoreError('checks', `The change set has errors: ${problems.join('; ')}`);
  }
  const documents = Object.fromEntries(set.flatMap(({ e }) => [[e.name, e.doc], [`${e.name}@${e.release}`, e.doc]]));
  const failing: string[] = [];
  for (const t of env.store.byKind('test').filter((t) => set.some(({ e }) => e.name === t.doc.target))) {
    const r = await runTest(env, t.doc, { documents });
    if (!r.passed) failing.push(t.name);
  }
  if (failing.length) {
    env.store.record({ tenant, actor: checker, action: 'publish', key: keys.join(','), outcome: 'refused', detail: `Red tests: ${failing.join(', ')}` });
    throw new StoreError('tests', `Publish needs green tests; red: ${failing.join(', ')}`);
  }
  // A workflow release is published with its signed engine package (R-09-23): without the studio's signing key,
  // nothing of the change set is published, and the refusal says why.
  if (set.some(({ e }) => e.kind === 'workflow')) {
    try {
      signingKey();
    } catch (err) {
      if (!(err instanceof SignatureRejected)) throw err;
      env.store.record({ tenant, actor: checker, action: 'publish', key: keys.join(','), outcome: 'refused', detail: err.message });
      throw new StoreError('signing', err.message);
    }
  }
  // The distributions and the configuration release the approval builds (R-11-40): asked of the permission table and
  // checked against the slot and parameter contracts before anything is published.
  let plan: Plan;
  try {
    plan = await planOnApproval(env, tenant, checker, docs);
  } catch (err: any) {
    env.store.record({ tenant, actor: checker, action: 'publish', key: keys.join(','), outcome: 'refused', detail: String(err?.message ?? err) });
    throw err;
  }
  // One commit on the content branch, or in pull-request mode one on the change set's branch and a
  // pull request (R-11-22, R-09-20, R-09-22). The change set keeps the id of its branch.
  // The change set takes the id of the approved draft's branch, across branches if its closure must (Issue I-44).
  const asked = requested.map((k) => env.store.entries.get(k)?.changeSet).find(Boolean);
  const id = env.store.changeSetIdFor(set.map((x) => x.e), asked);
  const branches = [...new Set(set.map((x) => x.e.changeSet).filter(Boolean) as string[])].sort((a, b) => (a === id ? -1 : b === id ? 1 : a.localeCompare(b)));
  const pullRequest = env.store.settings.mode === 'pull-request';
  return env.store.transaction(() => {
    const published = set.map(({ key, maker }) => ({ e: env.store.promote(tenant, key, maker, checker, id), maker }));
    const changeSet = {
      id, tenant, checker, publishedAt: published[0]?.e.publishedAt ?? new Date().toISOString(),
      makers: [...new Set(published.map((p) => p.maker))].sort(),
      members: published.map(({ e, maker }) => ({ kind: e.kind, name: e.name, release: e.release, key: e.key, maker })).sort((a, b) => a.key.localeCompare(b.key)),
    };
    const { files, skipped, tags } = filesFor(env, changeSet, env.store.settings);
    const bundles = buildPlanned(env, plan, changeSet);
    Object.assign(files, bundles.files);
    tags.push(...bundles.tags);
    const write = env.store.commitChangeSet(changeSet, branches, files, commitMessage(changeSet, skipped), { pullRequest, tags });
    return { changeSet, published: published.map((p) => p.e), write, skipped, pullRequest, bundles: bundles.built.map((b) => ({ kind: b.kind, releaseId: b.releaseId, tag: b.tag.name, folder: b.folder })) };
  });
}

// ---------------------------------------------------------- retirement (C19-S3)

/** Who pins a release: published workflow and hook releases whose closure holds it. */
export function pinnedBy(env: StudioEnv, name: string, release: string) {
  const checker = Checker.fromEnv(env);
  const out: { workflow: string; release: string }[] = [];
  for (const e of env.store.all().filter((x) => x.status === 'published' && (x.kind === 'workflow' || x.kind === 'hook'))) {
    const pins = new Map<string, string>((e.doc.artefacts ?? []).map((a: any) => [a.name, a.release]));
    const uses = pins.size ? pins.get(name) === release : pinnable(checker, e.doc).includes(name) && impliedPin(env, e, name) === release;
    if (uses) out.push({ workflow: e.name, release: e.release });
  }
  return out;
}

/**
 * A published workflow with no artefacts block pins what was published when it was: the latest
 * release of the line published no later than the workflow release itself.
 */
export function impliedPin(env: StudioEnv, wf: Entry, name: string): string | undefined {
  const at = wf.publishedAt ?? '';
  const rs = env.store.releasesOf(name).concat(env.store.all().filter((x) => x.name === name && x.status === 'retired')).filter((r) => (r.publishedAt ?? '') <= at || !r.publishedAt);
  rs.sort((a, b) => compareReleases(a.release, b.release));
  return rs[rs.length - 1]?.release;
}

/** The tests that exercise a workflow: those that target it, and end-to-end tests of the platform workflows a leg fills. */
export function testsOf(env: StudioEnv, wf: string) {
  const d = env.store.doc(wf);
  const hosts: string[] = d?.implements?.of ?? [];
  const scheme = d ? env.store.scheme(d.name.split('.').slice(0, 2).join('.')) : undefined;
  const startsIt = (scheme?.messages ?? []).some((m: any) => m.disposition?.start?.leg === wf || m.disposition?.start?.refuseLeg === wf);
  return env.store.byKind('test').filter((t) => t.doc.target === wf || (startsIt && t.doc.level === 'end-to-end' && hosts.includes(t.doc.target) && (t.doc.bench ?? []).some((b: any) => scheme?.ports?.includes(b.port))));
}

export function inFlight(env: StudioEnv, name: string, release: string): { count: number; asOf?: string } {
  const imp = env.store.extra.inFlight;
  const n = imp?.counts?.[`${name}@${release}`];
  return { count: n ?? 0, asOf: imp?.asOf };
}

/** Service operations supplies the in-flight counts per release; the studio does not read payments. */
export function importInFlight(env: StudioEnv, actor: string, counts: Record<string, number>, asOf: string) {
  enforce(env, actor, OWNER_TENANT, 'retire', 'in-flight', { kind: 'in-flight' });
  env.store.extra.inFlight = { counts, asOf };
  // One file each, never rewritten; the latest applies (R-11-23).
  env.store.addStateFile(inFlightPath(new Date().toISOString()), { kind: 'in-flight', inFlight: { counts, asOf } }, `In-flight counts as of ${asOf}\n\nSupplied by ${actor}.`, { tenant: OWNER_TENANT, actor, action: 'import-in-flight', key: 'in-flight', detail: `as of ${asOf}` });
}

export function retire(env: StudioEnv, actor: string, name: string, release: string) {
  enforce(env, actor, OWNER_TENANT, 'retire', `${name}@${release}`, { kind: 'release' });
  const pins = pinnedBy(env, name, release);
  const flying = inFlight(env, name, release);
  const why = pins.length ? `${name}@${release} is pinned by ${pins.map((p) => `${p.workflow}@${p.release}`).join(', ')} (R-01-23)` : flying.count ? `${flying.count} instance(s) are pinned to it as of ${flying.asOf} (R-01-23)` : undefined;
  if (why) {
    env.store.record({ tenant: OWNER_TENANT, actor, action: 'retire', key: `${name}@${release}`, outcome: 'refused', detail: why });
    throw new StoreError('R-01-23', why);
  }
  return env.store.retire(name, release, actor, OWNER_TENANT);
}

// -------------------------------------------- dependency release propagation (C05-S4)

/**
 * For a new release of a block: every workflow on that line that pins the block, its tests rerun
 * with the new release in place, and a proposed new workflow release with the new pin (R-01-18).
 */
export async function propagate(env: StudioEnv, name: string, newRelease: string) {
  const next = env.store.get(name, newRelease) ?? env.store.entries.get(`${name}@${newRelease}#draft`);
  if (!next) throw new StoreError('not-found', `${name}@${newRelease} does not exist`);
  const prevs = env.store.releasesOf(name).filter((r) => compareReleases(r.release, newRelease) < 0);
  const prev = prevs[prevs.length - 1];
  if (!prev) throw new StoreError('not-found', `No earlier release of ${name}`);
  const diff = interfaceDiff(prev.doc, next.doc);
  const bump = diff.required === 'major' ? 'major' : diff.required === 'minor' ? 'minor' : 'patch';
  const checker = Checker.fromEnv(env);
  const workflows = env.store.byKind('workflow').filter((w) => w.doc.wfKind !== 'subflow' && w.doc.wfKind !== 'segment' && pinnable(checker, w.doc).includes(name));
  const out: any[] = [];
  for (const w of workflows) {
    const tests = testsOf(env, w.name);
    const results: any[] = [];
    for (const t of tests) {
      const r = await runTest(env, t.doc, { supersessions: [{ artefact: name, replaces: prev.release, with: newRelease }] });
      results.push({ id: t.name, passed: r.passed, failed: r.checks.filter((c) => !c.passed).map((c) => c.what) });
    }
    const red = results.filter((r) => !r.passed);
    out.push({
      workflow: w.name, release: w.release, tests: results,
      proposed: red.length || bump === 'major' ? undefined : proposeRelease(w.release, bump),
      blockedBy: red.length ? `Red rerun: ${red.map((r) => r.id).join(', ')}` : bump === 'major' ? 'A major of a dependency is a new line; the workflow is not re-released' : !tests.length ? 'No test targets this workflow' : undefined,
      newPin: { name, release: newRelease, hash: next.hash },
    });
  }
  return { artefact: name, from: prev.release, to: newRelease, change: bump, workflows: out };
}

// -------------------------------------------------- supersession (C19-S4, C19-S5)

export function draftSupersession(env: StudioEnv, body: { artefact: string; replaces: string; with: string; reason: string; maker: string }) {
  enforce(env, body.maker, OWNER_TENANT, 'author', body.artefact, { kind: 'supersession', scope: parseName(body.artefact)?.scope });
  const pins = pinnedBy(env, body.artefact, body.replaces);
  const doc = {
    kind: 'supersession', id: `SUP-${new Date().getUTCFullYear()}-${String((env.store.byKind('supersession').length + (env.store.extra.supersessions?.length ?? 0) + 1)).padStart(3, '0')}`,
    artefact: body.artefact, replaces: body.replaces, with: body.with, reason: body.reason,
    scope: { workflowReleases: pins.map((p) => `${p.workflow}@${p.release}`), schemes: [...new Set(pins.map((p) => parseName(p.workflow)?.scope).filter((s) => s && s.includes('.') && !s.endsWith('.p')))] },
    evidence: { testRuns: [] as any[] },
    approval: { maker: body.maker, checker: '' },
  };
  return doc;
}

/** Rerun the pinned workflows' tests with the replacement swapped in, and attach the evidence. */
export async function testSupersession(env: StudioEnv, doc: any) {
  const runs: any[] = [];
  for (const wr of doc.scope?.workflowReleases ?? []) {
    const [wf, rel] = String(wr).split('@');
    const tests = testsOf(env, wf);
    const results: any[] = [];
    for (const t of tests) {
      const r = await runTest(env, t.doc, { supersessions: [{ artefact: doc.artefact, replaces: doc.replaces, with: doc.with }] });
      results.push({ id: t.name, passed: r.passed });
    }
    runs.push({ workflow: wf, release: rel, result: results.length && results.every((r) => r.passed) ? 'green' : results.length ? 'red' : 'no tests', tests: results });
  }
  return { ...doc, evidence: { testRuns: runs.map((r) => ({ workflow: r.workflow, release: r.release, result: r.result })) }, details: runs };
}

/** The hand-over: the document, its evidence and both approvals. The studio never applies it (R-01-21). */
export function handOverSupersession(env: StudioEnv, doc: any, checker: string) {
  enforce(env, checker, OWNER_TENANT, 'approve', doc.id, { kind: 'supersession', scope: parseName(doc.artefact)?.scope });
  if (!doc.approval?.maker || doc.approval.maker === checker) throw new StoreError('L27', 'Maker and checker must differ');
  if (!(doc.evidence?.testRuns ?? []).length || doc.evidence.testRuns.some((r: any) => r.result !== 'green')) throw new StoreError('R-01-20', 'A supersession needs a green rerun of every pinned workflow release in its scope');
  const { details, ...clean } = doc;
  void details;
  const final = { ...clean, approval: { maker: doc.approval.maker, checker } };
  const issues = env.schemas.validate(final);
  if (issues.length) throw new StoreError('L00', issues.map((i) => i.message).join('; '));
  if ((env.store.extra.supersessions ?? []).some((x: any) => x.id === final.id)) throw new StoreError('R-11-23', `${final.id} is already handed over`);
  env.store.extra.supersessions = [...(env.store.extra.supersessions ?? []), final];
  env.store.addStateFile(handedOverPath(final.id), final, `Hand over supersession ${final.id}\n\n${final.artefact} ${final.replaces} to ${final.with}. Maker ${final.approval.maker}, checker ${checker}.`, { tenant: OWNER_TENANT, actor: checker, action: 'hand-over-supersession', key: final.id, detail: `${final.artefact} ${final.replaces} to ${final.with}` });
  return { document: final, yaml: toYaml(final) };
}

// ------------------------------------------------ configuration sets (C18-S2)

/** The parameters a bank may set: those of shared content it accepts, and of its own artefacts. */
export function settableParameters(env: StudioEnv, tenant: string) {
  const out: any[] = [];
  for (const e of env.store.all().filter((x) => x.status === 'published' && x.doc?.parameters && (x.tenant === OWNER_TENANT || x.tenant === tenant))) {
    for (const [k, decl] of Object.entries<any>(e.doc.parameters)) if (decl.tenantSettable !== false) out.push({ parameter: `${e.name}#${k}`, owner: e.name, name: k, ...decl });
  }
  return out;
}

export function checkConfiguration(env: StudioEnv, tenant: string, doc: any): { code: string; severity: 'error' | 'warning'; message: string }[] {
  const issues: any[] = checkDocument(env, doc).map((i) => ({ code: i.code, severity: i.severity, message: i.message }));
  if (doc.tenant !== tenant) issues.push({ code: 'R-01-07', severity: 'error', message: `The set names tenant ${doc.tenant}, not ${tenant}` });
  const settable = new Set(settableParameters(env, tenant).map((p) => p.parameter));
  for (const v of doc.values ?? []) if (!settable.has(v.parameter)) issues.push({ code: 'L14', severity: 'error', message: `${v.parameter} is not a parameter this tenant may set` });
  // Overlapping periods for one parameter or one hook point are refused (C18-S2).
  const overlaps = (xs: any[], keyOf: (x: any) => string, what: string) => {
    const by = new Map<string, any[]>();
    for (const x of xs) {
      const k = keyOf(x);
      if (!by.has(k)) by.set(k, []);
      by.get(k)!.push(x);
    }
    for (const [k, list] of by) {
      list.sort((a, b) => (a.validFrom < b.validFrom ? -1 : 1));
      for (let i = 1; i < list.length; i++) {
        const prev = list[i - 1];
        if (!prev.validTo || prev.validTo >= list[i].validFrom) issues.push({ code: 'L14', severity: 'error', message: `${what} ${k}: the period from ${prev.validFrom} overlaps the one from ${list[i].validFrom}; close the first with validTo` });
      }
    }
  };
  overlaps(doc.values ?? [], (v) => v.parameter, 'Value');
  overlaps(doc.bindings ?? [], (b) => b.hookPoint, 'Binding');
  for (const x of [...(doc.values ?? []), ...(doc.bindings ?? [])]) if (x.validTo && x.validTo < x.validFrom) issues.push({ code: 'L14', severity: 'error', message: `A period ends before it starts (${x.validFrom} to ${x.validTo})` });
  return issues;
}

/**
 * A bank's dials and hook bindings: a configuration set, saved by a bank configurator as a draft on
 * its change set's branch. It needs no approval to save. It becomes the version the runtime is given
 * only when a bank approver who is not its maker approves the change set (R-11-31, R-06-04).
 */
export function saveConfiguration(env: StudioEnv, tenant: string, actor: string, yaml: string) {
  enforce(env, actor, tenant, 'author', `config:${tenant}`, { kind: 'configuration-set', scope: 'b' });
  if (typeof yaml !== 'string' || !yaml.trim()) throw new StoreError('L00', 'Send yaml: the configuration set');
  const doc = parseYaml(yaml);
  if (!doc || typeof doc !== 'object' || doc.kind !== 'configuration-set') throw new StoreError('L00', 'The YAML is not a configuration set');
  const issues = checkConfiguration(env, tenant, doc).filter((i) => i.severity === 'error');
  if (issues.length) throw new StoreError('L14', issues.map((i) => i.message).join('; '));
  const prev = env.store.configurationSet(tenant);
  if (prev && compareReleases(doc.release, prev.release) <= 0) throw new StoreError('R-06-15', `A changed configuration set needs a later release than ${prev.release}`);
  return env.store.saveDraft(tenant, actor, doc);
}

// ---------------------------------------------------- compatibility run (C18-S3)

/**
 * Before a tenant accepts a shared release: each bound hook point must still exist with a compatible
 * contract in the release, and each of the tenant's hook tests must pass against it.
 */
export async function compatibilityRun(env: StudioEnv, tenant: string, candidates: any[] = []) {
  const cs = env.store.configurationSet(tenant);
  const checker = Checker.fromEnv(env, candidates);
  const bindings: any[] = [];
  for (const b of cs?.bindings ?? []) {
    const [owner, hp] = String(b.hookPoint).split('#');
    const host = checker.byName.get(owner);
    const point = host ? checker.hookPoints(host)[hp] : undefined;
    const hook = env.store.doc(b.hook, b.release);
    let compatible = !!point;
    let reason = point ? undefined : `${owner} no longer declares hook point ${hp}`;
    if (point && hook && point.type !== hook.type) { compatible = false; reason = `The hook point is now ${point.type}; the hook is ${hook.type}`; }
    if (point && hook) {
      for (const st of (hook.do ?? [])) {
        const a = checker.byName.get(st.call);
        for (const w of a?.writes ?? []) if (point.type === 'transform' && !(point.contract?.writes ?? []).some((x: string) => w === x || w.startsWith(x + '.')) && !w.startsWith('facts.')) { compatible = false; reason = `${a.name} writes ${w}, outside the hook point's contract`; }
      }
    }
    bindings.push({ hookPoint: b.hookPoint, hook: `${b.hook}@${b.release}`, compatible, reason });
  }
  const tests = env.store.byKind('test').filter((t) => t.doc.tenant === tenant);
  const documents = Object.fromEntries(candidates.flatMap((d) => [[d.name, d], [`${d.name}@${d.release}`, d]]));
  const results: any[] = [];
  for (const t of tests) {
    const r = await runTest(env, t.doc, { documents });
    results.push({ id: t.name, target: t.doc.target, passed: r.passed, outcome: r.outcome, failed: r.checks.filter((c) => !c.passed) });
  }
  const accepted = bindings.every((b) => b.compatible) && results.every((r) => r.passed);
  return { tenant, candidates: candidates.map((c) => `${c.name}@${c.release}`), bindings, results, accepted };
}

// ----------------------------------------------------------- tenant export (C18-S4)

/** A tenant's export: the shared packs it accepted by hash, unchanged, its bank pack and its configuration set. */
export function tenantExport(env: StudioEnv, tenant: string) {
  const cs = env.store.configurationSet(tenant);
  const shared = (cs?.accepts ?? []).map((a: any) => {
    const b = buildPack(env, a.scope, 'shared');
    return { scope: a.scope, release: a.release, hash: 'sha256:' + require_sha(JSON.stringify(b.files)), files: Object.keys(b.files).length };
  });
  const bank = buildPack(env, 'b', 'bank', tenant);
  return { tenant, shared, bank: { files: Object.keys(bank.files).length, hash: 'sha256:' + require_sha(JSON.stringify(bank.files)) }, configuration: cs ? { release: cs.release, yaml: toYaml(cs) } : undefined };
}

import { createHash } from 'node:crypto';
import { AsyncLocalStorage } from 'node:async_hooks';
function require_sha(s: string) {
  return createHash('sha256').update(s, 'utf8').digest('hex');
}

// -------------------------------------------------- release gate report (C23-S3)

export interface Waiver { target: string; scenario: string; reason: string; approver: string }

/** The kit report per target with waivers; a red scenario that is not waived blocks the release. */
export async function conformanceReport(env: StudioEnv, target = 'studio') {
  const res = await runKit(env);
  const waivers: Waiver[] = (env.store.extra.waivers ?? []).filter((w: Waiver) => w.target === target);
  const engines = env.store.extra.engineReports ?? {};
  const rows = res.map((r) => ({ scenario: r.id, construct: r.construct, result: r.passed ? 'pass' : waivers.some((w) => w.scenario === r.id) ? 'waived' : 'fail', waiver: waivers.find((w) => w.scenario === r.id) }));
  const kit = env.store.all().find((e) => e.kind === 'conformance-kit')?.doc;
  return { target, kitRelease: kit?.release, date: kit?.date, rows, green: rows.every((r) => r.result !== 'fail'), engines };
}

export function addWaiver(env: StudioEnv, actor: string, w: Waiver) {
  enforce(env, actor, OWNER_TENANT, 'approve', `waiver:${w.scenario}`, { kind: 'waiver' });
  if (!w.reason) throw new StoreError('C23-S3', 'A waiver names its reason');
  // A waiver is its own change set: one commit with a record that holds it, and its audit (R-11-23).
  const waiver = { ...w, approver: actor };
  const id = env.store.changeSetIdFor([]);
  const changeSet = { id, tenant: OWNER_TENANT, checker: actor, publishedAt: new Date().toISOString(), makers: [actor], members: [], waivers: [waiver] };
  env.store.transaction(() => {
    env.store.markPublishing(id);
    env.store.record({ tenant: OWNER_TENANT, actor, action: 'waive', key: w.scenario, detail: `${w.target}: ${w.reason}`, changeSet: id });
    env.store.commitChangeSet(changeSet, [], {}, `Waive ${w.scenario} on ${w.target} (${id})\n\n${w.reason}\n\nApprover: ${actor}`);
  });
  env.store.extra.waivers = [...(env.store.extra.waivers ?? []), waiver];
  return { changeSet: id };
}

// ------------------------------------------------- users and roles, through maker-checker (R-11-23)

function mayAdminister(env: StudioEnv, actor: string): string | undefined {
  return permit(env, actor, OWNER_TENANT, 'author', { kind: 'users' });
}

/** Propose a new list of users and roles: a change set whose branch holds the new `governance/users.yaml`. */
export function proposeUsers(env: StudioEnv, maker: string, list: User[]) {
  const why = mayAdminister(env, maker);
  if (why) {
    env.store.record({ tenant: OWNER_TENANT, actor: maker, action: 'propose-users', key: 'users', outcome: 'refused', detail: why });
    throw new Forbidden(why);
  }
  if (!Array.isArray(list) || !list.length) throw new StoreError('R-11-23', 'Send the whole list of users');
  for (const u of list) {
    if (!u || typeof u.id !== 'string' || !/^[a-z][a-z0-9.-]*$/.test(u.id) || typeof u.tenant !== 'string' || !Array.isArray(u.roles) || u.roles.some((r) => !(ROLES as readonly string[]).includes(r)) || (u.subject !== undefined && (typeof u.subject !== 'string' || !u.subject)) || u.roles.some((r) => ROLE_TENANT[r as keyof typeof ROLE_TENANT] === 'bank' && u.tenant === OWNER_TENANT) || u.roles.some((r) => ROLE_TENANT[r as keyof typeof ROLE_TENANT] === 'owner' && u.tenant !== OWNER_TENANT)) {
      throw new StoreError('R-11-23', `Not a user: ${JSON.stringify(u)}. A user has an id (lower case, a role-shaped placeholder), a tenant, optionally the subject it signs in with, and roles from ${ROLES.join(', ')}; a bank role only in a bank tenant, a platform role only in the owner tenant`);
    }
  }
  // Each account signs in as one identity and works for exactly one tenant (R-11-34): no id or subject twice.
  const seen = new Map<string, string>();
  for (const u of list) {
    for (const key of [u.id, u.subject ?? u.id]) {
      if (seen.has(key) && seen.get(key) !== u.id) throw new StoreError('R-11-23', `${key} names two accounts (${seen.get(key)} and ${u.id}); an identity is one account in one tenant`);
      seen.set(key, u.id);
    }
  }
  if (new Set(list.map((u) => u.id)).size !== list.length) throw new StoreError('R-11-23', 'A user id appears twice');
  const id = env.store.proposeStateFile(USERS_PATH, { kind: 'studio-users', users: list }, maker, 'propose-users', 'users');
  return { changeSet: id };
}

/** Approve a proposed list of users: a different shared checker; the file is replaced in one commit with its change set. */
export function approveUsers(env: StudioEnv, checker: string, id: string) {
  const proposal = (env.store.extra.userProposals ?? []).find((p: any) => p.id === id);
  if (!proposal) throw new StoreError('not-found', `No proposed change to the users with change set ${id}`);
  enforce(env, checker, OWNER_TENANT, 'approve', `users:${id}`, { kind: 'users' });
  if (proposal.maker === checker) {
    env.store.record({ tenant: OWNER_TENANT, actor: checker, action: 'approve-users', key: 'users', outcome: 'refused', detail: 'Maker and checker must differ (R-06-04)', changeSet: id });
    throw new StoreError('R-06-04', 'Maker and checker must differ');
  }
  env.store.approveStateFile(id, USERS_PATH, { kind: 'studio-users', users: proposal.users }, proposal.maker, checker, 'approve-users');
  env.store.extra.users = proposal.users;
  env.store.extra.userProposals = (env.store.extra.userProposals ?? []).filter((p: any) => p.id !== id);
  return { changeSet: id, users: proposal.users };
}

/** Release of a shared pack (C19-S2): checks, green tests, and a green conformance report on every target in the binding list. */
export async function packReleaseGate(env: StudioEnv, scope: string) {
  const b = buildPack(env, scope, 'shared');
  const scheme = env.store.scheme(scope);
  const planned = (scheme?.messages ?? []).filter((m: any) => m.disposition?.planned).map((m: any) => m.id);
  const report = await conformanceReport(env, 'studio');
  return {
    scope,
    checks: b.refused ? { green: false, errors: b.refused } : { green: true },
    planned: planned.length ? { publishable: false, messages: planned, note: 'These message groups are not publishable; the rest of the pack may publish once they are removed or defined' } : { publishable: true },
    conformance: { target: 'studio', green: report.green, red: report.rows.filter((r) => r.result === 'fail').map((r) => r.scenario) },
    engines: report.engines,
  };
}

export { exportPackage, packageHash };
void ({} as Entry);
