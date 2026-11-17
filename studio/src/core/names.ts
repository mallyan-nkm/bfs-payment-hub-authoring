// Names and scopes (Part 1, R-01-01 to R-01-04).
// A name is <scope>.<type>.<name>.v<major>. Scopes are p, <cluster>.p, <cluster>.<scheme> and b.

export const ACTIVITY_TYPES = [
  'map', 'validate', 'enrich', 'screen', 'decide', 'reserve',
  'exchange', 'post', 'notify', 'hold', 'reconcile',
] as const;
export const OTHER_TYPES = ['rule', 'ruleset', 'table', 'sim', 'wf', 'seg', 'sub', 'handler', 'hook', 'port', 'ext'] as const;
export const ALL_TYPES: readonly string[] = [...ACTIVITY_TYPES, ...OTHER_TYPES];

export type ScopeLevel = 'platform' | 'cluster-platform' | 'scheme' | 'bank';

export interface ParsedName {
  name: string;
  scope: string;
  scopeLevel: ScopeLevel;
  cluster?: string;
  scheme?: string;
  type: string;
  local: string;
  major: number;
}

const CLUSTER = /^[a-z]{2,8}$/;
const SCHEME = /^[a-z0-9]{2,12}$/;

export function parseScope(scope: string): { level: ScopeLevel; cluster?: string; scheme?: string } | null {
  if (scope === 'p') return { level: 'platform' };
  if (scope === 'b') return { level: 'bank' };
  const parts = scope.split('.');
  if (parts.length !== 2) return null;
  const [cluster, second] = parts;
  if (!CLUSTER.test(cluster) || cluster === 'p' || cluster === 'b') return null;
  if (second === 'p') return { level: 'cluster-platform', cluster };
  if (!SCHEME.test(second) || second === 'b') return null;
  return { level: 'scheme', cluster, scheme: second };
}

/** Parse an artefact name. Returns null when it does not follow the grammar. */
export function parseName(name: string): ParsedName | null {
  const m = /^(.*)\.v(\d+)$/.exec(name);
  if (!m) return null;
  const major = Number(m[2]);
  const parts = m[1].split('.');
  // Try scope lengths 1 and 2, then a type, then a local name of one or more dotted segments.
  for (const scopeLen of [1, 2]) {
    if (parts.length < scopeLen + 2) continue;
    const scope = parts.slice(0, scopeLen).join('.');
    const sc = parseScope(scope);
    if (!sc) continue;
    const type = parts[scopeLen];
    if (!ALL_TYPES.includes(type)) continue;
    const local = parts.slice(scopeLen + 1).join('.');
    if (!/^[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)*$/.test(local)) continue;
    return { name, scope, scopeLevel: sc.level, cluster: sc.cluster, scheme: sc.scheme, type, local, major };
  }
  return null;
}

export function scopeOf(name: string): string | null {
  return parseName(name)?.scope ?? null;
}

/** Table 1.3: may an artefact in scope `from` depend on one in scope `to`? */
export function mayDependOn(from: string, to: string): boolean {
  if (from === to) return true;
  const f = parseScope(from);
  const t = parseScope(to);
  if (!f || !t) return false;
  if (t.level === 'bank') return f.level === 'bank';
  switch (f.level) {
    case 'platform':
      return t.level === 'platform';
    case 'cluster-platform':
      return t.level === 'platform' || (t.level === 'cluster-platform' && t.cluster === f.cluster);
    case 'scheme':
      return (
        t.level === 'platform' ||
        (t.level === 'cluster-platform' && t.cluster === f.cluster) ||
        (t.level === 'scheme' && t.cluster === f.cluster && t.scheme === f.scheme)
      );
    case 'bank':
      return true;
  }
}

export function isShared(scope: string): boolean {
  return scope !== 'b';
}

export function parseRelease(release: string): [number, number, number] | null {
  const m = /^(\d+)\.(\d+)\.(\d+)$/.exec(release);
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

export function compareReleases(a: string, b: string): number {
  const x = parseRelease(a) ?? [0, 0, 0];
  const y = parseRelease(b) ?? [0, 0, 0];
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] - y[i];
  return 0;
}
