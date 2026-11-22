import { describe, expect, it } from 'vitest';
import { durationMs } from '../src/core/duration.js';
import { runTest } from '../src/testing/runner.js';
import { freshEnv } from './helpers.js';

// Plan P2: the schedule-gated await of the posting windows. The inward legs wait for core banking's posting window, the bank's
// values (p.decide.posting-window.v1#windowStart and #windowEnd), asking again every fifteen minutes for up to a day; and the
// return to the employer's bank names the employer's account at payment level, so its posting credits it.

const WINDOW = (start: string, end: string) => [
  { parameter: 'p.decide.posting-window.v1#windowStart', value: start, validFrom: '2026-01-01' },
  { parameter: 'p.decide.posting-window.v1#windowEnd', value: end, validFrom: '2026-01-01' },
];

describe('the posting window (plan P2)', () => {
  it('is open by default: the credits post at once', async () => {
    const env = freshEnv();
    const r = await runTest(env, env.store.test('WPS-IN-002')!.doc);
    expect(r.passed).toBe(true);
    expect(r.trace).toContain('posting-window:open');
  });

  it('waits for a closed window to open, then credits; the trace shows one step', async () => {
    const env = freshEnv();
    const t = env.store.test('WPS-IN-002')!.doc;
    // The run starts at 06:00 UTC, 10:00 business time; the window opens at 15:00, five hours later.
    const r = await runTest(env, t, { values: WINDOW('15:00', '18:00') });
    expect(r.passed, JSON.stringify(r.checks?.filter((c: any) => !c.passed))).toBe(false); // the detail has the waits
    expect(r.outcome).toBe('settled');
    expect(r.trace).toEqual(t.expect.trace);
    const closed = r.detail.filter((d: string) => d === 'posting-window.window:closed').length;
    expect(closed).toBe(20); // 10:00 to 14:45, every fifteen minutes
    expect(r.detail).toContain('posting-window.window:open');
    expect(r.record!.elapsedMs).toBeGreaterThanOrEqual(durationMs('PT5H'));
  }, 60000);

  it('ends pending when the window never opens within the day', async () => {
    const env = freshEnv();
    const r = await runTest(env, { ...env.store.test('WPS-IN-002')!.doc, expect: { outcome: 'pending' } }, { values: WINDOW('bad', '18:00') });
    expect(r.outcome).toBe('pending');
    expect(r.trace).toContain('posting-window:closed');
    expect(r.trace.some((s: string) => s.startsWith('credit'))).toBe(false);
  }, 60000);
});

describe('the return to the employer\'s bank (plan P2, RTC)', () => {
  it('credits the employer\'s account, which the map names at payment level', async () => {
    const env = freshEnv();
    const r = await runTest(env, env.store.test('WPS-RTC-001')!.doc);
    expect(r.passed, JSON.stringify(r.checks?.filter((c: any) => !c.passed))).toBe(true);
    const leg = r.record!.instances.find((i) => i.workflow === 'uae.wps.wf.return-in.v1')!;
    expect(leg.context.payment.creditorAccount.iban).toBe('AE570330000000000000099');
    expect(leg.context.payment.debtorAccount.other).toBe('RTC-0000001234567-261118');
  });
});
