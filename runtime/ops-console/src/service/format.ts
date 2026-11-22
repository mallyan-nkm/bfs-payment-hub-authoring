// The service console's own logic, kept apart from the pages so the unit tests reach it.

export const PAGES = [
  { id: 'health', title: 'Health' },
  { id: 'content', title: 'Content' },
  { id: 'logs', title: 'Logs' },
  { id: 'errors', title: 'Errors and runbooks' },
  { id: 'actions', title: 'Actions' },
  { id: 'backup', title: 'Backup' },
  { id: 'assistant', title: 'Assistant' },
] as const;

/** The tone of a probe's or a release's state, for its badge. */
export function stateTone(s: string | undefined): 'pass' | 'warn' | 'fail' | '' {
  switch (s) {
    case 'ok': case 'active': case 'installed': case 'done': case 'verified': return 'pass';
    case 'warn': case 'draining': case 'scheduled': case 'pending-approval': case 'offered': return 'warn';
    case 'fail': case 'refused': case 'removed': return 'fail';
    default: return '';
  }
}

/** The web app's route of an error's runbook. Every error the console shows links to it. */
export function runbookHref(code: string): string {
  return `#/service/errors/${encodeURIComponent(code)}`;
}

/** The query of the log filter, without its empty fields. */
export function logQuery(f: Record<string, string | undefined>): Record<string, string> {
  return Object.fromEntries(Object.entries(f).filter(([, v]) => v !== undefined && v.trim() !== '').map(([k, v]) => [k, v!.trim()]));
}

/** Counts the changes of a difference by what changed. */
export function diffSummary(d: { flows?: any[]; artefacts?: any[]; parameters?: any[] } | null): string {
  if (!d) return '';
  const n = (xs: any[] | undefined, c: string) => (xs ?? []).filter((x) => x.change === c).length;
  const all = [...(d.flows ?? []), ...(d.artefacts ?? []), ...(d.parameters ?? [])];
  if (all.length === 0) return 'No difference against what is active.';
  return `${d.flows?.length ?? 0} flow(s), ${d.artefacts?.length ?? 0} file(s), ${d.parameters?.length ?? 0} parameter(s) differ: ${n(all, 'added')} added, ${n(all, 'changed')} changed, ${n(all, 'removed')} removed.`;
}

/** The hash route's query (#/service/actions?action=x&target=y), as the assistant's proposals link to it. */
export function hashQuery(hash: string): Record<string, string> {
  const q = hash.split('?')[1] ?? '';
  return Object.fromEntries(new URLSearchParams(q));
}

/** A short time for tables. */
export function when(iso: string | undefined): string {
  return iso ? iso.replace('T', ' ').replace(/\.\d+/, '').replace(/\+00:00$/, 'Z') : '—';
}
