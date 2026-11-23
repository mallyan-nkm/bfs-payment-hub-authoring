import { describe, expect, it } from 'vitest';
import { describeBreak, resolutionsFor } from '../../src/ops/Breaks';

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
});
