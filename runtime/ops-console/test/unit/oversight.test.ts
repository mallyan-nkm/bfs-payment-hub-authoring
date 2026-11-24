import { describe, expect, it } from 'vitest';
import { alertOffers, alertState, askedNote, ledgerGroups, ratesWarning, type LedgerBank, type Rates } from '../../src/ops/oversight';

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
