// What the fraud alerts, the ledger accounts and the rates warning show (R-05-90 to R-05-92), apart from React so the unit tests
// read it. The console's API decides everything; these only put its answers in words.

export interface Alert {
  item: string; itemKind: 'alert'; tenant: string; scheme: string; payment: string; link: string; reason?: string | null; step?: string | null;
  raisedAt: string; state: 'held' | 'released' | 'refused'; status?: string | null; closed: boolean; amount: number; currency: string;
  assignee?: string | null; assigneeName?: string | null; escalated: boolean; level: number;
}
export interface AlertList { total: number; open: number; rows: Alert[] }
export interface LedgerAccount { kind: string; title: string; activity: string; parameter: string; account?: string | null; set: boolean; configurationSet?: string | null; effectiveFrom?: string | null }
export interface LedgerBank { tenant: string; asOf: string; readOnly: boolean; accounts: LedgerAccount[] }
export interface RateRow { pair: string; rate: number | string; asOf?: string | null; age?: string | null; stale: boolean; standing: boolean }
export interface RatesBank { tenant: string; at: string; limit: string; limitSource: 'bank' | 'default'; stale: boolean; stalePairs: string[]; oldest?: string | null; rates: RateRow[]; code?: string | null }
export interface Rates { stale: boolean; banks: RatesBank[] }

/** An alert's state in words: what its payment is now, and whether the alert is still work. */
export function alertState(a: Pick<Alert, 'state' | 'closed'>): string {
  const now = a.state === 'held' ? 'Payment held' : a.state === 'released' ? 'Payment released' : 'Payment refused';
  return a.closed ? `${now}; closed` : now;
}

/** What an alert offers: the payment's own actions while it is held, and closing once its payment was decided. */
export function alertOffers(a: Pick<Alert, 'state' | 'closed'>): { release: boolean; refuse: boolean; close: boolean } {
  return { release: a.state === 'held' && !a.closed, refuse: a.state === 'held' && !a.closed, close: a.state !== 'held' && !a.closed };
}

/**
 * What the console says once a release or refusal of an alert's payment was asked (R-05-94, Issue I-279): carried out, or waiting for
 * the second approver the view names (a suspect payment's release waits for a supervisor).
 */
export function askedNote(what: 'release' | 'refuse', answer: { status?: string | null; secondApprover?: string | null } | null | undefined): string {
  const asked = what === 'release' ? 'Release of the payment asked' : 'Refusal of the payment asked';
  return answer?.status === 'pending-approval' ? `${asked}: it waits for a ${(answer.secondApprover ?? 'second approver').toLowerCase()}'s approval` : asked;
}

/** The "rates stale" warning, or null while every rate is within the bank's limit. */
export function ratesWarning(r: Rates | null): string | null {
  if (!r?.stale) return null;
  const parts = r.banks.filter((b) => b.stale).map((b) => `${b.tenant}: ${b.stalePairs.join(', ')} older than ${b.limit}`);
  return `Rates stale (${parts.join('; ')})`;
}

/** The ledger accounts of one bank, grouped by the accounting activity that names them, in the order of the four kinds. */
export function ledgerGroups(b: LedgerBank): { activity: string; accounts: LedgerAccount[] }[] {
  const order = ['settlement', 'suspense', 'income', 'vat'];
  const by = new Map<string, LedgerAccount[]>();
  for (const a of b.accounts) by.set(a.activity, [...(by.get(a.activity) ?? []), a]);
  return [...by.entries()].sort(([x], [y]) => x.localeCompare(y)).map(([activity, accounts]) => ({ activity, accounts: [...accounts].sort((p, q) => order.indexOf(p.kind) - order.indexOf(q.kind)) }));
}
