// A console's audit page (R-05-75, R-05-76, Issue I-202), shared by the operations and the service console: the console's own
// chain of facts, newest first, filtered by person, action and date, each row with its hash, and whether the chain verifies.

export interface AuditFilter { person?: string; action?: string; from?: string; to?: string }
export interface AuditRow { key: string; at: string; actor: string; by?: string; kind: string; code?: string; action?: string; subject?: string; seq: number; hash: string; prevHash?: string; why?: string }
export interface ChainState { ok: boolean; problem?: string | null; facts?: number; chains?: { subject: string; ok: boolean; facts: number; problem?: string | null }[] }

/** The query of the filter, without its empty fields. */
export function auditQuery(f: AuditFilter): Record<string, string> {
  return Object.fromEntries(Object.entries(f).filter(([, v]) => typeof v === 'string' && v.trim() !== '').map(([k, v]) => [k, (v as string).trim()]));
}

/** A fact as the page shows it: who (the actor and the name it recorded), what (kind, code and the system action), when, its hash. */
export function auditRow(f: any): AuditRow {
  const d = f?.data ?? {};
  const action = typeof d.name === 'string' ? d.name : typeof d.action === 'string' && !/^[0-9a-f]{32}$/.test(d.action) ? d.action : undefined;
  return {
    key: `${f.subject ?? ''}#${f.seq}`, at: f.at, actor: f.actor, by: typeof d.by === 'string' ? d.by : undefined, kind: f.kind, code: f.code ?? undefined, action,
    subject: f.subject, seq: f.seq, hash: f.hash ?? '', prevHash: f.prevHash ?? undefined, why: typeof d.why === 'string' ? d.why : undefined,
  };
}

/** Newest first, whatever order the API gave: by time, then by number. */
export function newestFirst(rows: AuditRow[]): AuditRow[] {
  return [...rows].sort((a, b) => (a.at < b.at ? 1 : a.at > b.at ? -1 : b.seq - a.seq));
}

/** A hash, short enough for a table; the whole hash is in the cell's title. */
export const shortHash = (h: string) => (h ? `${h.slice(0, 12)}…` : '—');

/** What the page says of the chain. */
export function chainText(c: ChainState | null | undefined): string {
  if (!c) return 'Not checked';
  if (c.ok) return c.chains ? `Every chain verifies (${c.chains.length} chain(s), ${c.chains.reduce((n, x) => n + x.facts, 0)} facts)` : `The chain verifies (${c.facts ?? 0} facts)`;
  const bad = c.chains?.filter((x) => !x.ok) ?? [];
  return bad.length ? `Does not verify: ${bad.map((x) => `${x.subject}: ${x.problem}`).join('; ')}` : `Does not verify: ${c.problem}`;
}
