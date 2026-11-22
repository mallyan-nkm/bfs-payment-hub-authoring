import { describe, expect, it } from 'vitest';
import { expandWorkflow } from '../src/core/expand.js';
import { runTest } from '../src/testing/runner.js';
import { withinBounds } from '../src/checks/checks.js';
import { clone, freshEnv } from './helpers.js';

// How long the bank waits for the scheme's acknowledgement is the bank's (R-05-47): the composite's parameter, looked up
// for the tenant on the day the await starts, its policy's replyTimeout the default. The scheme here answers after two days.

const WAIT = 'uae.wps.exchange.submit-salary-file.v1#ackWait';
const CALENDAR = 'uae.wps.exchange.submit-salary-file.v1#ackWaitCalendar';

function lateAck(env: ReturnType<typeof freshEnv>) {
  const t = clone(env.store.test('WPS-OUT-001')!.doc);
  t.bench = t.bench.map((b: any) => (b.port === 'uae.wps.port.wps.v1' ? { ...b, scenario: 'late-reply' } : b));
  t.expect = {};
  return t;
}
const outcome = (r: any) => r.trace.filter((x: string) => x.startsWith('end:') || x.startsWith('leg:')).pop();

describe('the acknowledgement wait is a bank value (R-05-47)', () => {
  it('expands to an await that names the parameter, the policy giving its default', () => {
    const env = freshEnv();
    const leg = expandWorkflow(env.store.latest('uae.wps.wf.salary-file-out.v1')!.doc, (n) => env.store.latest(n)?.doc);
    const aw = JSON.stringify(leg).match(/"await":\{[^}]*\}/)![0];
    expect(aw).toContain(`"timeout":"P1D"`);
    expect(aw).toContain(`"timeoutParameter":"${WAIT}"`);
    expect(aw).toContain(`"calendarParameter":"${CALENDAR}"`);
  });

  it('with the default of one business day the late acknowledgement is not waited for; with 72 elapsed hours it is', async () => {
    const env = freshEnv();
    const short = await runTest(env, lateAck(env));
    const long = await runTest(env, lateAck(env), {
      values: [
        { parameter: WAIT, value: 'PT72H', validFrom: '2026-01-01' },
        { parameter: CALENDAR, value: 'elapsed', validFrom: '2026-01-01' },
      ],
    });
    expect(short.trace.join(' | ')).not.toBe(long.trace.join(' | '));
    expect(outcome(long), long.trace.join(' | ')).toMatch(/settled/);
    expect(outcome(short), short.trace.join(' | ')).not.toMatch(/settled/);
  });

  it('a value from a later date does not reach a wait that starts before it', async () => {
    const env = freshEnv();
    const later = await runTest(env, lateAck(env), { values: [{ parameter: WAIT, value: 'PT72H', validFrom: '2027-01-01' }, { parameter: CALENDAR, value: 'elapsed', validFrom: '2027-01-01' }] });
    expect(outcome(later)).not.toMatch(/settled/);
  });

  it('bounds a duration by its min and max, a code by its allowed values, a string by its pattern, and each list item (R-06-21)', () => {
    const env = freshEnv();
    const sub = env.store.latest('uae.wps.exchange.submit-salary-file.v1')!.doc.parameters;
    const acc = env.store.latest('p.post.accounting.v1')!.doc.parameters;
    expect(withinBounds(sub.ackWait, 'PT72H')).toBe(true);
    expect(withinBounds(sub.ackWait, 'P30D')).toBe(false);
    expect(withinBounds(sub.ackWait, 'PT30M')).toBe(false);
    expect(withinBounds(sub.ackWaitCalendar, 'elapsed')).toBe(true);
    expect(withinBounds(sub.ackWaitCalendar, 'weekly')).toBe(false);
    expect(withinBounds(acc.transactionCode, 'SALARY')).toBe(true);
    expect(withinBounds(acc.transactionCode, 'not a code')).toBe(false);
    expect(withinBounds(acc.glAccountTypes, ['NOSTRO', 'SUSPENSE'])).toBe(true);
    expect(withinBounds(acc.glAccountTypes, ['nostro'])).toBe(false);
    expect(withinBounds(acc.chargeSlabs, [{ upTo: 1000, charge: 5 }])).toBe(true);
    expect(withinBounds(acc.chargeSlabs, [{ upTo: 1000, charge: -5 }])).toBe(false);
    expect(withinBounds(acc.vatPercent, 5)).toBe(true);
    expect(withinBounds(acc.vatPercent, 105)).toBe(false);
  });
});
