import { describe, expect, it } from 'vitest';
import { runTest } from '../src/testing/runner.js';
import { businessCalendarIn, businessCalendarOf } from '../src/core/reference.js';
import { holdExpiryAt } from '../src/interp/reference.js';
import { PartyService } from '../src/conformance/party.js';
import { freshEnv } from './helpers.js';

// A hold's expiry counts business days of the bank's business calendar (owner's decision of 23 November 2026, R-09-88, Issue
// I-127): the reference table of domain business-calendar, its weekend and its holidays, on the interpreter, in the party's
// answer to an engine and in the runtime's sweep (runtime/tests/TransactHub.Operations.Tests, HoldExpiryCalendarTests).

const env = freshEnv();
const calendarTable = () => env.store.latest('uae.p.ref.business-calendar.v1')!.doc;

const hold = (date: string) => ({
  kind: 'test', id: 'T-HOLD-CAL', title: 'An approval nobody decides expires on the business calendar', target: 'p.hold.approval.v1', level: 'activity',
  evaluationDate: date, tenant: 'example-bank',
  bench: [{ port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'never-decides' }],
  given: { context: { header: { paymentId: 'P-1', scheme: 'uae.wps' }, facts: { screening: {} } } },
  expect: { outcome: 'expired' },
});

describe('a hold expires on the bank\'s business calendar (R-09-88)', () => {
  it('reads the weekend and the holidays from the reference table in force', () => {
    const cal = businessCalendarOf(calendarTable(), '2026-11-23')!;
    expect(cal.weekend).toEqual([0, 6]);
    expect(cal.holidays).toEqual(expect.arrayContaining(['2026-12-01', '2026-12-02', '2026-12-03']));
    expect(businessCalendarOf(calendarTable(), '2026-01-01')).toBeUndefined();
    expect(businessCalendarIn([calendarTable()], '2026-11-23')?.table).toBe('uae.p.ref.business-calendar.v1');
  });

  it('counts whole days as business days and the rest as elapsed time', () => {
    const cal = businessCalendarOf(calendarTable(), '2026-11-23')!;
    const at = (s: string) => Date.parse(s);
    // Friday to Monday over the weekend; Monday 30 November to Friday 4 December over the three holidays.
    expect(new Date(holdExpiryAt(at('2026-11-27T06:00:00Z'), 'P1D', cal)).toISOString()).toBe('2026-11-30T06:00:00.000Z');
    expect(new Date(holdExpiryAt(at('2026-11-30T06:00:00Z'), 'P1D', cal)).toISOString()).toBe('2026-12-04T06:00:00.000Z');
    expect(new Date(holdExpiryAt(at('2026-11-30T06:00:00Z'), 'P1DT2H', cal)).toISOString()).toBe('2026-12-04T08:00:00.000Z');
    // Elapsed time without a calendar.
    expect(new Date(holdExpiryAt(at('2026-11-27T06:00:00Z'), 'P1D')).toISOString()).toBe('2026-11-28T06:00:00.000Z');
  });

  it('on the interpreter: an approval opened on a Friday expires on Monday, one opened before the holidays after them', async () => {
    const friday = await runTest(env, hold('2026-11-27'));
    expect(friday.passed, JSON.stringify(friday.checks)).toBe(true);
    expect(friday.elapsed).toBe('P3D');
    const holidays = await runTest(env, hold('2026-11-30'));
    expect(holidays.passed, JSON.stringify(holidays.checks)).toBe(true);
    expect(holidays.elapsed).toBe('P4D');
  });

  it('a bank that sets expiryCalendar to elapsed gets elapsed time', async () => {
    const t = hold('2026-11-27');
    const r = await runTest(env, t, { values: [{ parameter: 'p.hold.approval.v1#expiryCalendar', value: 'elapsed', validFrom: '2026-01-01' }] } as any);
    expect(r.passed, JSON.stringify(r.checks)).toBe(true);
    expect(r.elapsed).toBe('P1D');
  });

  it('the party answers an engine with the expiry, its calendar and the days, so every engine counts the same', async () => {
    const t = hold('2026-11-27');
    const party = new PartyService({ store: env.store, scripts: env.scripts, evaluationDate: t.evaluationDate, tenant: 'example-bank', bench: t.bench } as any);
    const res: any = await party.attempt({ instance: 'i-1', activity: 'p.hold.approval.v1', step: 'review', call: 1, attempt: 1, now: Date.parse('2026-11-27T06:00:00Z'), ctx: t.given.context });
    expect(res).toMatchObject({ kind: 'held', expiry: 'P1D', expiryCalendar: 'business', calendar: { weekend: [0, 6], table: 'uae.p.ref.business-calendar.v1' } });
    expect(res.calendar.holidays).toContain('2026-12-02');
  });
});
