import type { Entry } from '../core/store.js';
import type { RunnerEnv } from './runner.js';

// A draft run (R-09-230, Issue I-341). A designer runs an activity they have not published, and its tests, on the bench in
// their workspace: the tenant's drafts take the place of their published releases, as the checks already see them
// (`gov.draftSet`), in a copy of the store that writes nothing. The run's report says it was made with drafts and names them.
// Only the store a request is served from is ever exported, built into a distribution or approved: a draft is never in one.

export interface DraftRun {
  /** The drafts the run resolved, `<name>@<release>` (a test by its id). */
  drafts: string[];
  note: string;
}

/** The tenant's drafts in place of their published releases, in a copy of the store that writes nothing. */
export function withDrafts<E extends RunnerEnv>(env: E, tenant: string): { env: E; draftRun: DraftRun } {
  const drafts: Entry[] = env.store.drafts(tenant);
  const store = env.store.shadow();
  for (const d of drafts) {
    store.entries.delete(d.key);
    const key = d.key.replace(/#draft$/, '');
    // A published release is never edited (R-01-11): a draft is always of a release that is not published.
    if (store.entries.has(key)) continue;
    store.entries.set(key, { ...d, key, status: 'published' });
  }
  const names = drafts.map((d) => (d.kind === 'test' ? d.name : `${d.name}@${d.release}`)).sort();
  return {
    env: { ...env, store, scripts: env.scripts.over(() => store.scriptSources.values()) },
    draftRun: { drafts: names, note: `A draft run: made with ${names.length} draft(s) of ${tenant}, not published. Only published releases go into a distribution or an export.` },
  };
}

/** The tenant's draft of a test, by its id. */
export function draftTest(env: RunnerEnv, tenant: string, id: string): Entry | undefined {
  return env.store.drafts(tenant).find((e) => e.kind === 'test' && e.name === id);
}
