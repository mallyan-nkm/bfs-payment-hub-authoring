import { OWNER_TENANT, StoreError } from '../core/store.js';
import { parseYaml } from '../core/yaml.js';
import {
  banksTaking, buildConfigurationRelease, buildDistribution, bundleFiles, checkSlotContracts, classify, configurationReleasesOf, DIST_ROOT, DistributionRefused, licensedSchemes,
  nextConfigurationNumber, parameterContract, publishedDistributions, remember, type BuiltBundle,
} from '../export/distribution.js';
import { packEntries } from './packs.js';
import { enforce } from './governance.js';
import type { Target } from '../export/exporter.js';
import type { StudioEnv } from '../env.js';

// Who builds a bank's distribution and configuration release, and when (R-11-40, docs/runtime/PACKAGING-AND-DEPLOYMENT.md
// section 10 item 1). On approval of a change set as a version, unless the studio's settings turn it off (`distributions: false`; on by default, Issue I-113):
//   a platform or scheme change, approved by a Platform Approver: a new distribution for every bank that takes it;
//   a bank pack change, approved by that bank's Bank Approver: a new distribution of that bank;
//   a configuration change, approved by that bank's Bank Approver: a configuration release of that bank.
// Each is asked of the permission table (permissions.ts: `approve` on `distribution` and `configuration-release`), and
// goes into the change set's own commit with its tag. The same builds are offered on their own (POST /api/distributions,
// POST /api/configuration-releases) to the same roles.

/** The ids already used on the content branch for a bank on a day: its distribution folders. */
async function usedIds(env: StudioEnv, bank: string, day: string): Promise<Set<number>> {
  const used = new Set<number>();
  const c = env.store.content;
  if (c?.head) {
    const snap = await c.snapshot(c.head);
    const prefix = `${DIST_ROOT}/${bank}/${day}-`;
    for (const p of snap.files.keys()) if (p.startsWith(prefix)) used.add(Number(p.slice(prefix.length).split('/')[0]));
  }
  for (const p of (env.store.extra.distributions ?? []) as string[]) if (p.startsWith(`${bank}/${day}-`)) used.add(Number(p.split('-').pop()));
  return used;
}

/** The next distribution id of a bank: `YYYY.MM.DD-n` (R-09-48). */
export async function nextId(env: StudioEnv, bank: string, date = new Date().toISOString().slice(0, 10)): Promise<string> {
  const day = date.replace(/-/g, '.');
  const used = await usedIds(env, bank, day);
  let n = 1;
  while (used.has(n)) n++;
  return `${day}-${n}`;
}

/**
 * The number of a bank's next configuration release (R-11-115, Issue I-340): after every configuration release of the bank on
 * the branch's head and every one built since, so that each approval that builds one gives it a release of its own.
 */
export async function nextConfigurationRelease(env: StudioEnv, bank: string, setRelease: string): Promise<string> {
  const c = env.store.content;
  const paths = c?.head ? (await c.snapshot(c.head)).files.keys() : [];
  return nextConfigurationNumber(setRelease, configurationReleasesOf(env, bank, paths));
}

/** The distributions of a bank on the branch, read from the head commit's tree (folders outside what the studio keeps in memory). */
async function branchDistributions(env: StudioEnv, bank: string) {
  const out = publishedDistributions(env, bank);
  const c = env.store.content;
  if (!c?.head) return out;
  const snap = await c.snapshot(c.head);
  for (const [p, blob] of snap.files) {
    const m = new RegExp(`^${DIST_ROOT}/${bank}/([^/]+)/manifest\\.yaml$`).exec(p);
    if (!m || out.some((x) => x.id === m[1])) continue;
    const doc = parseYaml((await c.client.getBlob(blob)).toString('utf8'));
    out.push({ releaseId: `dist/${bank}@${m[1]}`, id: m[1], manifest: doc, parameterContract: doc.parameterContract });
  }
  return out.sort((a, b) => a.id.localeCompare(b.id, undefined, { numeric: true }));
}

export interface Plan {
  distributions: { bank: string; id: string }[];
  configuration: { bank: string; number: string; against: { releaseId: string; parameterContract: any }[] }[];
}

/**
 * Before a change set is approved: what it builds, asked of the permission table, and the slot and parameter contracts
 * checked with its documents in place, so that a mismatch refuses the approval before anything is published.
 */
export async function planOnApproval(env: StudioEnv, tenant: string, checker: string, docs: any[]): Promise<Plan> {
  const plan: Plan = { distributions: [], configuration: [] };
  if (env.store.settings.distributions === false) return plan;
  const kinds = classify(docs.map((d) => ({ kind: d.kind, name: d.name ?? `${d.scope}.scheme` })), tenant);
  const day = new Date().toISOString().slice(0, 10);
  const want = new Set<string>();
  if (kinds.shared.length) {
    for (const s of kinds.shared) enforce(env, checker, OWNER_TENANT, 'approve', `distribution:${s}`, { kind: 'distribution', scope: s });
    banksTaking(env, kinds.shared).forEach((b) => want.add(b));
  }
  if (kinds.bankPack) {
    enforce(env, checker, tenant, 'approve', `distribution:${tenant}`, { kind: 'distribution', scope: 'b' });
    want.add(tenant);
  }
  for (const bank of [...want].sort()) {
    precheck(env, bank, docs);
    plan.distributions.push({ bank, id: await nextId(env, bank, day) });
  }
  if (kinds.values) {
    enforce(env, checker, tenant, 'approve', `configuration-release:${tenant}`, { kind: 'configuration-release', scope: 'b' });
    const against = (await branchDistributions(env, tenant)).map((d) => ({ releaseId: d.releaseId, parameterContract: d.parameterContract }));
    // A configuration release is checked against a distribution's parameter contract. With distributions on by default (Issue
    // I-113), a bank that has none yet gets its first in the same approval, by the same bank approver, and the values are
    // checked against it in place.
    if (against.length === 0 && !plan.distributions.some((d) => d.bank === tenant)) {
      enforce(env, checker, tenant, 'approve', `distribution:${tenant}`, { kind: 'distribution', scope: 'b' });
      precheck(env, tenant, docs);
      plan.distributions.push({ bank: tenant, id: await nextId(env, tenant, day) });
    }
    // The values checked now, against the contracts the release will name, so a value no distribution declares, or one out of
    // its bounds, refuses the approval before anything is published. A distribution this approval builds is checked in place.
    const set = docs.find((d) => d.kind === 'configuration-set');
    const bindings = docs.find((d) => d.kind === 'port-bindings');
    // Each approval that builds a configuration release gives it a release of its own (R-11-115, Issue I-340).
    const number = await nextConfigurationRelease(env, tenant, (set ?? env.store.configurationSet(tenant))?.release ?? '1.0.0');
    if ((set || bindings) && !plan.distributions.some((d) => d.bank === tenant)) buildConfigurationRelease(env, { bank: tenant, number, against, set, bindings: bindings?.bindings });
    plan.configuration.push({ bank: tenant, number, against });
  }
  return plan;
}

/** The slot and parameter contracts of a bank's next distribution, with the change set's documents in place of their published releases. */
function precheck(env: StudioEnv, bank: string, docs: any[]) {
  // The change set's lock file in place of the one in force: a scheme it licenses is in the distribution it builds (R-11-76).
  const lock = docs.find((d) => d.kind === 'pack' && d.scope === 'b' && d.tenant === bank);
  const schemes = lock ? [...lock.licensedSchemes].sort() : licensedSchemes(env, bank);
  const scopeOf = (n: string) => (n.split('.')[0] === 'p' || n.split('.')[0] === 'b' ? n.split('.')[0] : n.split('.').slice(0, 2).join('.'));
  const overlay = (scope: string) => {
    const base = packEntries(env, scope, scope === 'b' ? bank : undefined).map((e) => e.doc).filter((d) => !docs.some((x) => x.kind === d.kind && x.name === d.name));
    return [...base, ...docs.filter((d) => d.name && scopeOf(d.name) === scope)];
  };
  const platform = ['p', ...new Set(schemes.map((s) => `${s.split('.')[0]}.p`))].flatMap(overlay);
  const slot = checkSlotContracts(platform, schemes.map((s) => ({ scope: s, docs: overlay(s) })));
  const params = parameterContract([...platform, ...schemes.flatMap(overlay), ...overlay('b')]);
  const problems = [...slot.problems, ...params.problems];
  if (problems.length) throw new DistributionRefused(`The approval would build a distribution of ${bank} that is refused: ${problems.join('; ')}`);
}

/** In the approval's transaction, with the members published: the bundles the plan names, as files and tags of the change set's commit. */
export function buildPlanned(env: StudioEnv, plan: Plan, changeSet: any): { files: Record<string, string>; tags: { name: string; message: string }[]; built: BuiltBundle[] } {
  const built: BuiltBundle[] = [];
  for (const d of plan.distributions) built.push(buildDistribution(env, { bank: d.bank, engine: env.store.settings.target as Target, id: d.id, changeSets: [changeSet.id], withSimulators: env.store.settings.testDistributions === true }));
  for (const c of plan.configuration) {
    const against = [...c.against, ...built.filter((b) => b.bank === c.bank).map((b) => ({ releaseId: b.releaseId, parameterContract: b.manifest.parameterContract }))];
    // The configuration set and the port bindings the studio last released (R-11-47), with this change set's members published.
    built.push(buildConfigurationRelease(env, { bank: c.bank, number: c.number, changeSet: changeSet.id, against }));
  }
  built.forEach((b) => remember(env, b));
  return { files: Object.assign({}, ...built.map(bundleFiles)), tags: built.map((b) => b.tag), built };
}

/**
 * A distribution or a configuration release built on its own, by the roles that may approve one, published as one commit
 * with its tag. A platform approver builds any bank's distribution; a bank approver its own bank's and its configuration
 * releases.
 */
export async function buildAndPublish(env: StudioEnv, actor: string, tenant: string, req: { kind: 'distribution' | 'configuration'; bank: string; engine?: Target; withSimulators?: boolean; bindings?: any[]; calendars?: any[]; release?: string }) {
  if (req.kind === 'distribution') {
    if (tenant === OWNER_TENANT) enforce(env, actor, OWNER_TENANT, 'approve', `distribution:${req.bank}`, { kind: 'distribution', scope: 'p' });
    else if (tenant === req.bank) enforce(env, actor, tenant, 'approve', `distribution:${req.bank}`, { kind: 'distribution', scope: 'b' });
    else throw new StoreError('R-06-01', `${actor} works for ${tenant}, not ${req.bank}`);
  } else {
    if (tenant !== req.bank) throw new StoreError('R-06-01', `A configuration release of ${req.bank} is approved by its own bank approver`);
    enforce(env, actor, tenant, 'approve', `configuration-release:${req.bank}`, { kind: 'configuration-release', scope: 'b' });
  }
  const b = req.kind === 'distribution'
    ? buildDistribution(env, { bank: req.bank, engine: req.engine ?? (env.store.settings.target as Target), id: await nextId(env, req.bank), withSimulators: req.withSimulators })
    : buildConfigurationRelease(env, { bank: req.bank, release: req.release, number: req.release ? undefined : await nextConfigurationRelease(env, req.bank, env.store.configurationSet(req.bank)?.release ?? '1.0.0'), against: (await branchDistributions(env, req.bank)).map((d) => ({ releaseId: d.releaseId, parameterContract: d.parameterContract })), bindings: req.bindings, calendars: req.calendars });
  const c = env.store.content;
  if (!c) throw new StoreError('content', 'The studio is not connected to a content branch');
  const files = bundleFiles(b);
  const write = await c.write(c.conn.branch, `${b.kind === 'distribution' ? 'Distribution' : 'Configuration release'} ${b.releaseId}\n\n${b.tag.message}\n\nBuilt by ${actor}.`,
    Object.entries(files).map(([path, text]) => ({ path, text, guard: 'create' as const })), { tags: [b.tag] });
  remember(env, b);
  env.store.record({ tenant, actor, action: `build-${b.kind}`, key: b.releaseId, detail: `${b.tag.name}: ${Object.keys(b.files).length} file(s)` });
  return { releaseId: b.releaseId, id: b.id, tag: b.tag.name, folder: b.folder, files: Object.keys(b.files).length, manifest: b.manifest, git: write };
}
