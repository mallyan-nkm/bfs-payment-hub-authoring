import type { StudioEnv } from '../env.js';

// Publish check on tests (C11-S5, R-03-16, R-03-23): the outcomes of an artefact that no test
// expects. A workflow's outcomes are its exit outcomes; an activity's and a composite's are its outcomes.

export function untestedOutcomes(env: StudioEnv, name: string) {
  const d = env.store.doc(name);
  if (!d) return undefined;
  const outcomes: string[] = d.kind === 'activity' ? (d.outcomes ?? []).map((o: any) => o.name) : d.kind === 'workflow' || d.kind === 'hook' ? d.exit?.outcomes ?? d.outcomes ?? [] : [];
  const tests = env.store.byKind('test').filter((t) => t.doc.target === name);
  const covered = new Set(tests.map((t) => t.doc.expect?.outcome).filter(Boolean));
  return { name, outcomes, tests: tests.map((t) => t.name), untested: outcomes.filter((o) => !covered.has(o)) };
}

export function testCoverageReport(env: StudioEnv) {
  const rows: any[] = [];
  for (const e of env.store.all().filter((x) => x.status === 'published' && (x.kind === 'activity' || (x.kind === 'workflow' && !['segment', 'subflow'].includes(x.doc.wfKind)) || x.kind === 'hook'))) {
    const r = untestedOutcomes(env, e.name);
    if (r) rows.push({ ...r, kind: e.kind, scope: e.scope });
  }
  return rows.sort((a, b) => a.name.localeCompare(b.name));
}
