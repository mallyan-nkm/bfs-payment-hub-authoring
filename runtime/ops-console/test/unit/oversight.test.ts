import { describe, expect, it } from 'vitest';
import { alertOffers, alertState, askedNote, deliveryNote, ledgerGroups, ratesWarning, ruleTraceNote, undeliveredState, type LedgerBank, type Rates } from '../../src/ops/oversight';

// The fraud alerts, the ledger accounts and the rates warning in words (R-05-90 to R-05-92). Synthetic values only.

describe('the fraud alerts (R-05-90)', () => {
  it("offers the payment's release and refusal while it is held, and closing once it was decided", () => {
    expect(alertOffers({ state: 'held', closed: false })).toEqual({ release: true, refuse: true, close: false });
    expect(alertOffers({ state: 'refused', closed: false })).toEqual({ release: false, refuse: false, close: true });
    expect(alertOffers({ state: 'released', closed: true })).toEqual({ release: false, refuse: false, close: false });
  });

  it("says what the alert's payment is now, and whether the alert is closed", () => {
    expect(alertState({ state: 'held', closed: false })).toBe('Payment held');
    expect(alertState({ state: 'refused', closed: true })).toBe('Payment refused; closed');
  });

  it("says that a suspect payment's release waits for a supervisor's second approval (R-05-94, I-279)", () => {
    expect(askedNote('release', { status: 'pending-approval', secondApprover: 'Supervisor' })).toBe("Release of the payment asked: it waits for a supervisor's approval");
    expect(askedNote('release', { status: 'done' })).toBe('Release of the payment asked');
    expect(askedNote('refuse', { status: 'done' })).toBe('Refusal of the payment asked');
  });
});

describe('the ledger accounts (R-05-91)', () => {
  it('groups the accounts by their accounting activity, settlement first and VAT last', () => {
    const b: LedgerBank = {
      tenant: 'example-bank', asOf: '2026-11-23T08:00:00Z', readOnly: true,
      accounts: [
        { kind: 'vat', title: 'VAT', activity: 'p.post.accounting.v2', parameter: 'p.post.accounting.v2#vatAccount', set: false },
        { kind: 'settlement', title: 'Settlement (nostro)', activity: 'p.post.accounting.v2', parameter: 'p.post.accounting.v2#settlementAccount', account: 'GL-NOSTRO-0001', set: true },
        { kind: 'settlement', title: 'Settlement (nostro)', activity: 'p.post.accounting.v1', parameter: 'p.post.accounting.v1#settlementAccount', account: 'GL-NOSTRO-0002', set: true },
      ],
    };
    const g = ledgerGroups(b);
    expect(g.map((x) => x.activity)).toEqual(['p.post.accounting.v1', 'p.post.accounting.v2']);
    expect(g[1].accounts.map((a) => a.kind)).toEqual(['settlement', 'vat']);
  });
});

describe('the rates warning (R-05-92)', () => {
  const bank = (stale: boolean): Rates['banks'][number] => ({ tenant: 'example-bank', at: '2026-11-23T08:00:00Z', limit: 'PT24H', limitSource: 'default', stale, stalePairs: stale ? ['AED/EUR'] : [], rates: [] });
  it('says nothing while every rate is within the limit', () => {
    expect(ratesWarning({ stale: false, banks: [bank(false)] })).toBeNull();
    expect(ratesWarning(null)).toBeNull();
  });
  it('names each stale pair and the limit', () => {
    expect(ratesWarning({ stale: true, banks: [bank(true)] })).toBe('Rates stale (example-bank: AED/EUR older than PT24H)');
  });
});

describe("a file the File API never delivered (R-05-103, Issue I-337)", () => {
  it('says whether the File API still tries or its attempts ended, with the error', () => {
    expect(undeliveredState({ state: 'failing', attempts: 1, code: 'port.CircuitOpen' })).toBe('Not delivered yet: 1 attempt failed (port.CircuitOpen), the File API tries again');
    expect(undeliveredState({ state: 'undelivered', attempts: 5, code: 'file.DeliveryFailed' })).toBe('Never delivered: the attempts ended after 5 attempts (file.DeliveryFailed)');
  });
  it("puts a delivery's facts on the timeline in words, and nothing for any other fact", () => {
    expect(deliveryNote({ kind: 'file.delivered', code: 'sftp', data: { fileName: 'F1.SIF', folder: 'centralBank/upload/cb/wps/sif', adapter: 'sftp', resend: true } }))
      .toBe('F1.SIF delivered to centralBank/upload/cb/wps/sif on sftp (resent)');
    expect(deliveryNote({ kind: 'file.delivery-failed', code: 'port.CircuitOpen', data: { fileName: 'F1.SIF', attempt: 2, why: 'the breaker is open' } }))
      .toBe('F1.SIF not delivered, attempt 2: the breaker is open');
    expect(deliveryNote({ kind: 'file.undelivered', code: 'file.Undelivered', data: { fileName: 'F1.SIF', attempts: 5 } }))
      .toBe("F1.SIF never delivered: the File API's attempts ended after 5; resend it once the partner answers");
    expect(deliveryNote({ kind: 'step', code: 'accepted', data: {} })).toBeNull();
  });
  it("shows a step's rule trace from its fact (R-09-240, Issue I-304)", () => {
    expect(ruleTraceNote({ kind: 'step', data: { ruleTrace: { worst: 'reject', rules: [{ rule: 'p.rule.batch-currency-present.v1', fired: true }, { rule: 'p.rule.amount-positive.v1', fired: false }] } } }))
      .toBe('rules: p.rule.batch-currency-present.v1 (worst reject)');
    expect(ruleTraceNote({ kind: 'step', data: {} })).toBeNull();
  });
});
