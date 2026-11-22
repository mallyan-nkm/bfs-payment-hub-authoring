import * as path from 'node:path';
import { canonicalJson } from '../core/canonical.js';
import { compareReleases } from '../core/names.js';
import { toYaml } from '../core/yaml.js';
import { refsOf, checkAll } from '../checks/checks.js';
import { zip } from '../export/zip.js';
import { OWNER_TENANT, type Entry } from '../core/store.js';
import type { StudioEnv } from '../env.js';

// Packs (Part 11; C05-S2, C19-S1). A pack is all artefacts of one scope with a manifest that lists
// name, kind, release, hash and path, and the packs it depends on. A pack that holds a simulator
// is never promotable (R-07-12). A test pack holds tests, simulators and their stubs.

function scopeOfName(n: string): string {
  const parts = n.split('.');
  return parts[0] === 'p' || parts[0] === 'b' ? parts[0] : parts.slice(0, 2).join('.');
}

/**
 * The pack an entry belongs to: seeded content the folder of its source in spec/ (R-11-25); a bank's
 * own folder on the branch its bank pack; else its scope.
 */
export function packScopeOf(e: Entry): string {
  if (e.source === 'spec') {
    const parts = e.path.split(/[\\/]/);
    if (parts[0] === 'examples' && parts[1] === 'bank') return 'b';
    if (parts[0] === 'factory' && parts[1]) return parts[1];
    // A bank pack loaded from its own folder (Store.loadBankPack): everything in it is the bank's.
    if (parts[0] === 'banks' && e.tenant !== OWNER_TENANT) return 'b';
  }
  return e.scope;
}

function parents(scope: string): string[] {
  if (scope === 'p' || scope === 'b') return [];
  const [cluster, second] = scope.split('.');
  return second === 'p' ? ['p'] : ['p', `${cluster}.p`];
}

export function packEntries(env: StudioEnv, scope: string, tenant?: string): Entry[] {
  const latest = new Map<string, Entry>();
  for (const e of env.store.all()) {
    if (e.status !== 'published' || ['pack', 'inventory', 'conformance-kit', 'engine-binding', 'configuration-set', 'supersession'].includes(e.kind)) continue;
    if (packScopeOf(e) !== scope) continue;
    if (scope === 'b' && tenant && e.tenant !== tenant) continue;
    const k = `${e.kind}:${e.name}`;
    const prev = latest.get(k);
    if (!prev || compareReleases(e.release, prev.release) > 0) latest.set(k, e);
  }
  return [...latest.values()];
}

/** A bank's pack manifest in the repository: the one of its tenant, the example bank's when none is named. */
function bankPack(env: StudioEnv, tenant?: string): Entry | undefined {
  return env.store.byKind('pack').find((e) => e.doc.scope === 'b' && (e.doc.tenant ?? 'example-bank') === (tenant ?? 'example-bank'));
}

/** The pack manifest of a scope, as tools/build_factory.py writes it (C05-S2). A bank pack's paths are relative to its own folder. */
export function packManifest(env: StudioEnv, scope: string, tenant?: string): any {
  const entries = packEntries(env, scope, tenant);
  entries.sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
  const own = scope === 'b' ? bankPack(env, tenant) : undefined;
  const folder = scope === 'b' ? (own ? path.dirname(own.path) : path.join('examples', 'bank')) : path.join('factory', scope);
  const arts = entries.map((e) => ({
    name: e.kind === 'scheme' ? `${scope}.scheme` : e.name,
    kind: e.kind,
    release: e.release || '1.0.0',
    hash: e.hash,
    path: e.source === 'spec' ? path.relative(folder, e.path) : `${e.kind}/${e.name}.yaml`,
  }));
  const hasSim = entries.some((e) => e.kind === 'simulator');
  // A shared pack depends on what table 1.3 lets its scope depend on; a bank pack on the scopes its
  // artefacts reach, with their own parents.
  const deps = new Set<string>(parents(scope));
  if (scope === 'b') {
    // Its tests are in its lock file, so what they reach is a dependency too (Issue I-43, "Found, not fixed").
    for (const e of entries) {
      for (const r of refsOf(e.doc)) {
        const s = scopeOfName(r);
        if (s !== 'b' && s !== 'ck') [s, ...parents(s)].forEach((x) => deps.add(x));
      }
    }
  }
  const order = ['p', 'uae.p', 'uae.wps'];
  const dependsOn = [...deps].sort((a, b) => (order.indexOf(a) + 99) % 99 - (order.indexOf(b) + 99) % 99 || a.localeCompare(b)).map((s) => ({ scope: s, release: '1.0.0' }));
  const pack: any = {
    kind: 'pack', scope, release: '1.0.0',
    title: scope === 'b' ? own?.doc.title ?? 'Example bank pack' : `Factory pack for ${scope}`,
    packType: scope === 'b' ? 'bank' : 'shared',
    origin: scope === 'b' ? 'authored' : 'factory',
  };
  if (scope === 'b') pack.tenant = tenant ?? 'example-bank';
  if (dependsOn.length) pack.dependsOn = dependsOn;
  pack.artefacts = arts;
  pack.containsSimulators = hasSim;
  pack.promotable = !hasSim;
  return pack;
}

/** Does the generated manifest equal the one in the repository? A bank pack is named by its tenant. */
export function compareManifest(env: StudioEnv, scope: string, tenant?: string): { equal: boolean; differences: string[] } {
  const want = (scope === 'b' ? bankPack(env, tenant) : env.store.byKind('pack').find((e) => e.doc.scope === scope))?.doc;
  const got = packManifest(env, scope, want?.tenant);
  if (!want) return { equal: false, differences: ['No manifest in the repository'] };
  const diffs: string[] = [];
  for (const k of ['scope', 'release', 'packType', 'tenant', 'containsSimulators', 'promotable']) if (JSON.stringify(want[k]) !== JSON.stringify(got[k])) diffs.push(`${k}: repository ${JSON.stringify(want[k])}, generated ${JSON.stringify(got[k])}`);
  if (canonicalJson(want.dependsOn ?? []) !== canonicalJson(got.dependsOn ?? [])) diffs.push(`dependsOn: repository ${JSON.stringify(want.dependsOn)}, generated ${JSON.stringify(got.dependsOn)}`);
  const w = new Map<string, any>((want.artefacts ?? []).map((a: any) => [a.name, a]));
  const g = new Map<string, any>((got.artefacts ?? []).map((a: any) => [a.name, a]));
  for (const [n, a] of w) if (!g.has(n)) diffs.push(`${n} is in the repository manifest only`);
  else if (g.get(n).hash !== a.hash || g.get(n).release !== a.release) diffs.push(`${n}: hash or release differs`);
  for (const n of g.keys()) if (!w.has(n)) diffs.push(`${n} is in the generated manifest only`);
  return { equal: diffs.length === 0, differences: diffs };
}

export interface PackBuild { manifest: any; files: Record<string, string>; refused?: string[] }

/**
 * Build a pack for export (C19-S1): shared, bank or test. The shared and bank packs carry no
 * simulator and no test; the test pack carries tests, simulators and conformance fixtures and is
 * never promotable. The build is refused while any check is red.
 */
export function buildPack(env: StudioEnv, scope: string, kind: 'shared' | 'bank' | 'test', tenant?: string): PackBuild {
  const errors = checkAll(env).filter((i) => i.severity === 'error' && (scopeOfName(i.key) === scope || i.key === scope));
  const all = packEntries(env, scope, tenant);
  const entries = kind === 'test' ? all.filter((e) => e.kind === 'test' || e.kind === 'simulator') : all.filter((e) => e.kind !== 'test' && e.kind !== 'simulator');
  const files: Record<string, string> = {};
  for (const e of entries.sort((a, b) => a.name.localeCompare(b.name))) files[`${e.kind}/${e.name}@${e.release || '1.0.0'}.yaml`] = toYaml(e.doc);
  const hasSim = entries.some((e) => e.kind === 'simulator');
  const manifest = {
    kind: 'pack', scope, release: '1.0.0', title: `${kind === 'test' ? 'Test' : kind === 'bank' ? 'Bank' : 'Shared'} pack for ${scope}`,
    packType: kind, origin: scope === 'b' ? 'authored' : 'factory', ...(tenant && scope === 'b' ? { tenant } : {}),
    artefacts: entries.map((e) => ({ name: e.kind === 'scheme' ? `${scope}.scheme` : e.name, kind: e.kind, release: e.release || '1.0.0', hash: e.hash, path: `${e.kind}/${e.name}@${e.release || '1.0.0'}.yaml` })),
    containsSimulators: hasSim, promotable: !hasSim && kind !== 'test',
  };
  files['pack.yaml'] = toYaml(manifest);
  return errors.length ? { manifest, files: {}, refused: errors.map((e) => `${e.code} ${e.key}: ${e.message}`) } : { manifest, files };
}

export function packZip(b: PackBuild): Buffer {
  return zip(Object.entries(b.files).sort(([a], [c]) => a.localeCompare(c)).map(([name, content]) => ({ name, content: Buffer.from(content, 'utf8') })));
}
