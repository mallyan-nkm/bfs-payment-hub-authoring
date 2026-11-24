// The service console's own logic, kept apart from the pages so the unit tests reach it.

export const PAGES = [
  { id: 'health', title: 'Health' },
  { id: 'content', title: 'Content' },
  { id: 'logs', title: 'Logs' },
  { id: 'errors', title: 'Errors and runbooks' },
  { id: 'actions', title: 'Actions' },
  { id: 'backup', title: 'Backup' },
  // What the runtime trusts: the signers of content, each with what it may sign and the rates feed's key (R-09-249, Issue I-292), and
  // each distribution's native modules by their SHA-256 (R-09-120 to R-09-123).
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

/** The kinds a package may be signed as (R-09-184). */
export const SIGNED_KINDS = ['workflow-package', 'distribution', 'configuration'] as const;
/** The runtime's one feed (R-09-177): its key signs configuration releases of the feed only. */
export const RATES_FEED = 'fx-rates';
/** The target of a rotation that retires no key: the rates feed's first key, or another signer's (R-09-249). */
export const RETIRE_NONE = 'none';

/** What a trusted key may sign, in words: every kind where it names none, else its kinds, and its feed where it is scoped. */
export function keyMaySign(k: { kinds?: string[]; everyKind?: boolean; scope?: { feed?: string } | null }): string {
  const what = k.everyKind ? 'every kind' : (k.kinds ?? []).join(', ') || 'nothing';
  return k.scope?.feed ? `${what}, of the ${k.scope.feed} feed only` : what;
}

/**
 * The parameters of a rotation as the trust page asks for them (R-09-249): the new key's PEM, the kinds it may sign (all of them is
 * every kind, so none is sent), and a feed's scope, which only a key that signs configuration releases alone may take. A string when
 * something is missing or wrong, which the page says instead of asking.
 */
export function rotationParameters(publicKey: string, kinds: string[], feed: string): Record<string, unknown> | string {
  if (!publicKey.includes('BEGIN PUBLIC KEY')) return 'Paste the new key\'s public key, as PEM';
  if (kinds.length === 0) return 'Choose at least one kind the key may sign';
  const every = SIGNED_KINDS.every((k) => kinds.includes(k));
  if (feed.trim() && !(kinds.length === 1 && kinds[0] === 'configuration')) return 'A key scoped to a feed signs configuration releases only';
  return { publicKey: publicKey.trim(), ...(every ? {} : { kinds: SIGNED_KINDS.filter((k) => kinds.includes(k)) }), ...(feed.trim() ? { scope: { feed: feed.trim() } } : {}) };
}

/** The action that trusts the rates feed's own key (R-09-249): no PEM pasted, the key before it retired if there is one. */
export function feedKeyAction(feed: { previous?: string | null }): { action: string; target: string; parameters: Record<string, unknown> } {
  return { action: 'rotate-trusted-key', target: feed.previous || RETIRE_NONE, parameters: { feed: RATES_FEED } };
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
