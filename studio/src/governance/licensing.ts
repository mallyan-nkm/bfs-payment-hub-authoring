import { canonicalJson } from '../core/canonical.js';
import { compareReleases } from '../core/names.js';
import { OWNER_TENANT, StoreError, type Entry } from '../core/store.js';
import { packManifest } from './packs.js';
import { enforce } from './governance.js';
import type { StudioEnv } from '../env.js';

// Licensing a scheme to a bank from the studio (R-11-75 to R-11-78, Issue I-205). The schemes a bank licenses are its bank
// pack's `licensedSchemes` (R-11-37). A Bank Configurator proposes a new release of the lock file that licenses a further
// scheme: a draft of kind `pack` in the bank's change set. A Bank Approver who is not its maker approves it (R-11-35, R-06-04);
// the approval publishes the new lock file at a path of its own on the content branch (insert-only, R-11-23) and builds the
// bank's next distribution with it (R-11-40). At every approval of a bank's change set, each scheme its configuration release
// binds must be licensed (R-11-77).

const isBankPack = (e: Entry, tenant: string) => e.doc?.scope === 'b' && (e.doc.tenant ?? 'example-bank') === tenant;

/** The bank's lock file in force: its latest published release. */
export function lockFileOf(env: StudioEnv, tenant: string): Entry | undefined {
  return env.store.byKind('pack').filter((e) => isBankPack(e, tenant)).sort((a, b) => compareReleases(a.release || '1.0.0', b.release || '1.0.0')).pop();
}

const nextMinor = (r: string) => r.replace(/^(\d+)\.(\d+)\..*/, (_: string, M: string, m: string) => `${M}.${Number(m) + 1}.0`);
const isScheme = (env: StudioEnv, s: string) => env.store.byKind('scheme').some((e) => e.doc.scope === s);
const scopeOfName = (n: string) => (n.split('.')[0] === 'p' || n.split('.')[0] === 'b' ? n.split('.')[0] : n.split('.').slice(0, 2).join('.'));

/**
 * The schemes a bank's configuration set binds (R-11-77): those it accepts, those it enables as a rail
 * (`p.table.rail-choice.v1#enabledRails`), those whose artefacts' dials it sets, and those whose hook points it binds a hook to.
 * Only scopes that are schemes of the factory count; a platform scope is not a scheme.
 */
export function schemesBound(env: StudioEnv, set: any): string[] {
  if (!set) return [];
  const out = new Set<string>();
  for (const a of set.accepts ?? []) if (a?.scope) out.add(String(a.scope));
  for (const v of set.values ?? []) {
    const owner = String(v?.parameter ?? '').split('#')[0];
    out.add(scopeOfName(owner));
    if (String(v?.parameter) === 'p.table.rail-choice.v1#enabledRails' && Array.isArray(v.value)) for (const r of v.value) out.add(String(r));
  }
  for (const b of set.bindings ?? []) out.add(scopeOfName(String(b?.hookPoint ?? '').split('#')[0]));
  return [...out].filter((s) => s.includes('.') && !s.endsWith('.p') && isScheme(env, s)).sort();
}

/** The schemes a configuration set binds that the licence list does not hold. */
export function unlicensed(env: StudioEnv, set: any, licensed: string[]): string[] {
  return schemesBound(env, set).filter((s) => !licensed.includes(s));
}

/** What the bank workspace shows: the lock file in force, every scheme of the factory with whether it is licensed, and the proposal in progress. */
export function licensing(env: StudioEnv, tenant: string) {
  const lock = lockFileOf(env, tenant);
  const licensed: string[] = Array.isArray(lock?.doc.licensedSchemes) ? [...lock!.doc.licensedSchemes].sort() : [];
  const draft = env.store.drafts(tenant).find((d) => d.kind === 'pack' && isBankPack(d, tenant));
  const bound = schemesBound(env, env.store.configurationSet(tenant));
  return {
    bank: tenant,
    release: lock?.release ?? null,
    licensedSchemes: licensed,
    schemes: env.store.byKind('scheme').filter((e) => e.tenant === OWNER_TENANT).map((e) => ({
      scope: e.doc.scope, title: e.doc.title, release: e.release, provenance: e.doc.provenance?.status,
      licensed: licensed.includes(e.doc.scope), bound: bound.includes(e.doc.scope),
    })).sort((a, b) => a.scope.localeCompare(b.scope)),
    draft: draft ? { key: draft.key, release: draft.release, licensedSchemes: draft.doc.licensedSchemes ?? [], status: draft.status, changeSet: draft.changeSet ?? null, maker: draft.maker ?? null } : null,
  };
}

/**
 * A Bank Configurator proposes the bank's next lock file, licensing one more scheme (R-11-75): the lock file the studio writes
 * (as `npm run studio -- bank-pack` does) at the next minor of the release in force, saved as a draft. It needs no approval to
 * save; it is in force only once a Bank Approver who is not its maker approves it.
 */
export function proposeLicence(env: StudioEnv, tenant: string, actor: string, scheme: string) {
  if (tenant === OWNER_TENANT) throw new StoreError('R-06-03', 'A scheme is licensed to a bank, by the bank');
  enforce(env, actor, tenant, 'author', `pack:b:${tenant}`, { kind: 'pack', scope: 'b' });
  if (typeof scheme !== 'string' || !scheme) throw new StoreError('L00', 'Name the scheme to license');
  if (!isScheme(env, scheme)) throw new StoreError('R-11-37', `${scheme} is no scheme of the factory`);
  const lock = lockFileOf(env, tenant);
  if (!lock) throw new StoreError('bank-pack', `${tenant} has no bank pack`);
  const draft = env.store.drafts(tenant).find((d) => d.kind === 'pack' && isBankPack(d, tenant));
  const have: string[] = draft ? draft.doc.licensedSchemes ?? [] : lock.doc.licensedSchemes ?? [];
  if (have.includes(scheme)) throw new StoreError('R-11-75', `${tenant} ${draft ? 'already proposes' : 'already licenses'} ${scheme}`);
  const release = draft?.release ?? nextMinor(lock.release || '1.0.0');
  const doc = packManifest(env, 'b', tenant, { licensed: [...have, scheme], release });
  return env.store.saveDraft(tenant, actor, doc);
}

/**
 * A bank pack lock file a change set would publish (R-11-76): the bank's own, a later release than the one in force, licensing
 * schemes of the factory only, and exactly the lock file the studio writes for it now, so a lock file written by hand, or one
 * made stale by a later release of the bank's artefacts, is refused.
 */
export function lockFileProblems(env: StudioEnv, tenant: string, doc: any): string[] {
  const problems: string[] = [];
  if (doc?.scope !== 'b' || doc.packType !== 'bank' || doc.tenant !== tenant) return [`A change set of ${tenant} publishes only ${tenant}'s own bank pack lock file`];
  const lock = lockFileOf(env, tenant);
  if (lock && compareReleases(doc.release, lock.release) <= 0) problems.push(`The lock file's release ${doc.release} is not later than ${lock.release} in force (R-11-35)`);
  const licensed: string[] = Array.isArray(doc.licensedSchemes) ? doc.licensedSchemes : [];
  if (!licensed.length) problems.push('The lock file names no licensedSchemes (R-11-37)');
  const unknown = licensed.filter((s) => !isScheme(env, s));
  if (unknown.length) problems.push(`The lock file licenses ${unknown.join(', ')}, which is no scheme of the factory (R-11-37)`);
  if (!unknown.length && licensed.length) {
    const want = packManifest(env, 'b', tenant, { licensed, release: doc.release });
    if (canonicalJson(want) !== canonicalJson(doc)) problems.push(`The lock file is not the one the studio writes for ${tenant} now: propose the licence again`);
  }
  return problems;
}
