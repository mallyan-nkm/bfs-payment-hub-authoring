// The service console's own logic, kept apart from the pages so the unit tests reach it.

export const PAGES = [
  { id: 'health', title: 'Health' },
  { id: 'content', title: 'Content' },
  { id: 'logs', title: 'Logs' },
  { id: 'errors', title: 'Errors and runbooks' },
  { id: 'actions', title: 'Actions' },
  { id: 'backup', title: 'Backup' },
  // What the runtime trusts: the signers of content, and each distribution's native modules by their SHA-256 (R-09-120 to R-09-123).
  { id: 'trust', title: 'Signers and modules' },
  { id: 'assistant', title: 'Assistant' },
  // The console's own audit trail (R-05-76, Issue I-202).
  { id: 'audit', title: 'Audit' },
] as const;

/** The tone of a probe's or a release's state, for its badge. */
export function stateTone(s: string | undefined): 'pass' | 'warn' | 'fail' | '' {
  switch (s) {
    case 'ok': case 'active': case 'installed': case 'done': case 'verified': case 'in-force': case 'in use': return 'pass';
    case 'warn': case 'draining': case 'scheduled': case 'pending-approval': case 'offered': case 'awaiting-consent': return 'warn';
    case 'fail': case 'refused': case 'removed': case 'revoked': return 'fail';
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

/**
 * A step's error as the log names it when the error dictionary has no entry for it, so no runbook link stands for it (Issue
 * I-350): its name and whether the engine may retry it, from the log item's unmasked error. Empty when there is none, or when a
 * linked entry shows it.
 */
export function stepError(error: { name?: string; retryable?: boolean | null } | null | undefined, errors: { code: string }[] | undefined): string {
  if (!error?.name || (errors ?? []).some((x) => x.code === error.name)) return '';
  return error.retryable == null ? error.name : `${error.name}, ${error.retryable ? 'retryable' : 'not retryable'}`;
}
