import type { BulkResult, WorkRow } from './api';

// What the work queue shows and sends (R-05-71), apart from React so the unit tests read it: the key of a row, the items of a
// selection, who holds a row, and a bulk change's answer in words. A bulk change is sent as the items it names; the API applies
// each item's own rules and answers item by item.

export const FILTERS = [
  { id: 'all', label: 'All' },
  { id: 'mine', label: 'Mine' },
  { id: 'unassigned', label: 'Unassigned' },
  { id: 'escalated', label: 'Escalated' },
] as const;

export const keyOf = (r: Pick<WorkRow, 'itemKind' | 'item'>) => `${r.itemKind}:${r.item}`;

export function selectionItems(rows: WorkRow[], selected: Set<string>): { kind: string; id: string }[] {
  return rows.filter((r) => selected.has(keyOf(r))).map((r) => ({ kind: r.itemKind, id: r.item }));
}

/** The words for a role an item escalates to (R-05-107). */
const ROLE_WORDS: Record<string, string> = { Supervisor: 'supervisors', BankApprover: 'bank approvers' };

export function holder(r: WorkRow): string {
  // By hand to the supervisors; by time, after the bank's escalateAfter, to the role the view's escalateTo names (R-05-107).
  if (r.escalated) return `Escalated to ${ROLE_WORDS[r.escalatedTo ?? 'Supervisor'] ?? r.escalatedTo} (level ${r.level})${r.automatic ? ', after waiting too long' : ''}`;
  return r.assigneeName ?? r.assignee ?? 'Unassigned';
}

export function bulkSummary(r: BulkResult): string {
  const refused = r.results.filter((x) => !x.ok);
  const why = [...new Set(refused.map((x) => x.error).filter(Boolean))];
  return `${r.done} done, ${r.refused} refused${why.length ? `: ${why.join('; ')}` : ''}`;
}

/** A saved filter's query without the paging and the empty terms, as the list's hash holds it. */
export function queryToSave(q: Record<string, string>): Record<string, string> {
  return Object.fromEntries(Object.entries(q).filter(([k, v]) => v !== '' && !['page', 'pageSize'].includes(k)));
}
