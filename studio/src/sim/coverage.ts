import type { StudioEnv } from '../env.js';

// Coverage of a scheme's reason codes by simulator scenarios (R-07-08, C10-S4). The code list of
// a scheme is the codes its own rules yield and the codes of the rules its rule sets reuse.
// Each code has a scenario that returns it, or a recorded waiver; otherwise it is open.

export function reasonCodeCoverage(env: StudioEnv, scope: string) {
  const codes = new Map<string, Set<string>>();
  const add = (c: string, by: string) => {
    if (!codes.has(c)) codes.set(c, new Set());
    codes.get(c)!.add(by);
  };
  const sets = env.store.byKind('ruleset').filter((e) => e.scope === scope);
  const rules = new Set<string>(env.store.byKind('rule').filter((e) => e.scope === scope).map((e) => e.name));
  for (const s of sets) for (const r of s.doc.rules ?? []) rules.add(r);
  for (const r of rules) for (const t of env.store.doc(r)?.then ?? []) if (t.yield?.finding?.reasonCode) add(t.yield.finding.reasonCode, r);
  const sims = env.store.byKind('simulator').filter((e) => e.scope === scope);
  // The codes the scheme's own simulators return belong to its list too.
  for (const sm of sims) for (const sc of sm.doc.scenarios ?? []) for (const st of sc.steps ?? []) if (st.reasonCode) add(st.reasonCode, sm.name);
  const waivers: any[] = (env.store.extra.coverageWaivers ?? []).filter((w: any) => w.scope === scope);
  const rows = [...codes].sort(([a], [b]) => a.localeCompare(b)).map(([code, by]) => {
    const scenarios: string[] = [];
    for (const s of sims) for (const sc of s.doc.scenarios ?? []) if ((sc.steps ?? []).some((st: any) => st.reasonCode === code)) scenarios.push(`${s.name}#${sc.name}`);
    const waiver = waivers.find((w) => w.code === code);
    return { code, rules: [...by].sort(), scenarios, waiver, state: scenarios.length ? 'covered' : waiver ? 'waived' : 'open' };
  });
  return { scope, rows, covered: rows.filter((r) => r.state === 'covered').map((r) => r.code), open: rows.filter((r) => r.state === 'open').map((r) => r.code) };
}
