import { describe, expect, it } from 'vitest';
import { bulkSummary, holder, keyOf, queryToSave, selectionItems } from '../../src/ops/work';
import type { WorkRow } from '../../src/ops/api';

// The work queue's own logic (R-05-71): a selection becomes the items a bulk change names, each row says who holds it, and a
// bulk answer is put in words item by item.

const row = (over: Partial<WorkRow>): WorkRow => ({
  item: 'P-1', itemKind: 'payment', tenant: 'example-bank', payment: 'P-1', what: 'hold', waitingSince: '2026-11-24T08:00:00Z', escalated: false, level: 0, ...over,
});

describe('the work queue (R-05-71)', () => {
  it('sends the selected items only, a payment and an action told apart', () => {
    const rows = [row({}), row({ item: 'A-1', itemKind: 'action', payment: 'P-1' }), row({ item: 'P-2', payment: 'P-2' })];
    const sel = new Set([keyOf(rows[0]), keyOf(rows[1])]);
    expect(selectionItems(rows, sel)).toEqual([{ kind: 'payment', id: 'P-1' }, { kind: 'action', id: 'A-1' }]);
  });

  it('says who holds an item: a person, the supervisors after an escalation, or nobody', () => {
    expect(holder(row({ assignee: 'op-1', assigneeName: 'Example operator' }))).toBe('Example operator');
    expect(holder(row({ escalated: true, level: 2 }))).toBe('Escalated to supervisors (level 2)');
    expect(holder(row({}))).toBe('Unassigned');
  });

  it('puts a bulk answer in words, with each reason a refusal gave once', () => {
    expect(bulkSummary({ done: 1, refused: 2, results: [{ ok: true }, { ok: false, error: 'is with op-2' }, { ok: false, error: 'is with op-2' }] })).toBe('1 done, 2 refused: is with op-2');
  });

  it('saves a filter without its paging or empty terms', () => {
    expect(queryToSave({ scheme: 'uae.wps', hold: 'open', page: '2', pageSize: '50', q: '' })).toEqual({ scheme: 'uae.wps', hold: 'open' });
  });
});
