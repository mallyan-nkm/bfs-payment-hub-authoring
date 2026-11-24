import { describe, expect, it } from 'vitest';
import { RESOLUTION_LABELS, describeBreak, describeOrder, resolutionsFor } from '../../src/ops/Breaks';

// The reconciliation break queue (R-09-92): what each break says, and which resolutions it offers.

describe('the break queue', () => {
  it('says what each kind of break is', () => {
    expect(describeBreak({ kind: 'amount', expected: '500', reported: '499.00' })).toBe('The amount differs: 500 here, 499.00 in the report');
    expect(describeBreak({ kind: 'not-in-store' })).toMatch(/no record/);
    expect(describeBreak({ kind: 'status', expected: 'RCVD', reported: 'ACSC' })).toMatch(/RCVD here, ACSC in the report/);
  });

  it('offers adopting the scheme\'s status only for a known payment the report gives a status for, and nothing once asked', () => {
    expect(resolutionsFor({ state: 'open', payment: 'p-1', reportedStatus: 'ACSC' })).toEqual(['explain', 'adopt-status']);
    expect(resolutionsFor({ state: 'open', payment: null, reportedStatus: 'ACSC' })).toEqual(['explain']);
    expect(resolutionsFor({ state: 'pending-consent', payment: 'p-1', reportedStatus: 'ACSC' })).toEqual([]);
    expect(resolutionsFor({ state: 'resolved', payment: 'p-1', reportedStatus: 'ACSC' })).toEqual([]);
  });

  it('offers the orders the scheme names, and says what became of one (R-09-250, Issue I-168)', () => {
    expect(resolutionsFor({ state: 'open', payment: null, reportedStatus: null, offered: ['explain', 'return'] })).toEqual(['explain', 'return']);
    expect(resolutionsFor({ state: 'pending-consent', payment: null, reportedStatus: null, offered: ['explain', 'return'] })).toEqual([]);
    expect(RESOLUTION_LABELS['exception-request']).toBe('Start an exception request');
    expect(RESOLUTION_LABELS.return).toBe('Start a return');
    expect(describeOrder({ state: 'to-start' })).toMatch(/File API starts it/);
    expect(describeOrder({ state: 'started', payment: 'uae-wps-1' })).toBe('Order started: uae-wps-1');
    expect(describeOrder({ state: 'refused', code: 'frontdoor.NoActiveDistribution', why: 'none active' })).toBe('Order refused (frontdoor.NoActiveDistribution): none active');
    expect(describeOrder(null)).toBeNull();
  });
});
