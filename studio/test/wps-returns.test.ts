import { describe, expect, it } from 'vitest';
import { runTest } from '../src/testing/runner.js';
import { checkDocument } from '../src/checks/checks.js';
import { evalExpr } from '../src/rules/expr.js';
import { durationMs } from '../src/core/duration.js';
import type { StudioEnv } from '../src/env.js';
import { freshEnv } from './helpers.js';

// The agent's returns (Issues I-39 to I-41, decided 2026-11-22): the amount rule, the returns exchange's wait and
// upload hold as the bank's values, and the acknowledgement told apart by the family of the file it answers.

let shared: StudioEnv | undefined;
const env = () => (shared ??= freshEnv());
const RETURN = 'uae.wps.exchange.return-credits.v1';
const ROUTING = { parameter: 'uae.wps.rule.agent-is-this-bank.v1#ownRoutingCodes', value: ['302620122'], validFrom: '2026-01-01' };
const test = (id: string) => env().store.all().find((e) => e.kind === 'test' && e.name === id)!.doc;

describe('the returns amount (I-39, item 4)', () => {
  it('rounds a line to two decimals half away from zero, and sums the rounded lines', () => {
    expect(evalExpr('round2(1500.005)', {})).toBe(1500.01);
    expect(evalExpr('round2(1999.995)', {})).toBe(2000);
    expect(evalExpr('round2(0 - 0.005)', {})).toBe(-0.01);
    expect(evalExpr('round2(2.675)', {})).toBe(2.68);
    const items = [{ ok: false, v: 1500.005 }, { ok: true, v: 7 }, { ok: false, v: 1999.995 }];
    // The sum of the rounded lines is 3500.01; the rounded sum would be 3500.
    expect(evalExpr('sumWhere(items, i => not i.ok, i => round2(i.v))', { items })).toBe(3500.01);
    expect(evalExpr('round2(sumWhere(items, i => not i.ok, i => i.v))', { items })).toBe(3500);
  });
});

describe('the returns exchange: the wait and the upload hold are the bank\'s values (I-41, items 3 and 4)', () => {
  it('declares both as bounded duration parameters with the source\'s defaults', () => {
    const p = env().store.doc(RETURN).parameters;
    expect(p.replyWait).toMatchObject({ type: 'duration', default: 'P30D', bounds: { min: 'P1D', max: 'P30D' } });
    expect(p.replyWaitCalendar).toMatchObject({ type: 'code', default: 'elapsed', bounds: { allowed: ['elapsed', 'business'] } });
    expect(p.uploadHold).toMatchObject({ type: 'duration', default: 'PT0S', bounds: { min: 'PT0S', max: 'PT10M' } });
  });

  it('waits thirty calendar days by default (I-51, the source\'s 2,592,000 s), and the example bank\'s ten business days when it sets them', async () => {
    const byDefault = await runTest(env(), { ...test('WPS-IN-004'), tenant: undefined, expect: { outcome: 'pending' } }, { values: [ROUTING] });
    expect(byDefault.passed, JSON.stringify(byDefault.checks)).toBe(true);
    expect(byDefault.record!.elapsedMs).toBeGreaterThanOrEqual(durationMs('P30D'));
    expect(byDefault.record!.elapsedMs).toBeLessThan(durationMs('P31D'));
    // On the business calendar the same thirty would be about six weeks from Tuesday 17 November 2026.
    const business = await runTest(env(), { ...test('WPS-IN-004'), tenant: undefined, expect: { outcome: 'pending' } }, { values: [ROUTING, { parameter: `${RETURN}#replyWaitCalendar`, value: 'business', validFrom: '2026-01-01' }] });
    expect(business.record!.elapsedMs).toBeGreaterThanOrEqual(durationMs('P42D'));
    const bank = await runTest(env(), test('WPS-IN-004'));
    expect(bank.passed, JSON.stringify(bank.checks)).toBe(true);
    expect(bank.record!.elapsedMs).toBeLessThan(durationMs('P15D'));
  }, 60000);

  it('holds the upload for the bank\'s upload hold', async () => {
    const r = await runTest(env(), test('WPS-IN-002'), { values: [{ parameter: `${RETURN}#uploadHold`, value: 'PT10M', validFrom: '2026-01-01' }] });
    expect(r.detail).toContain('return-credits.upload-hold:PT10M');
    expect(r.outcome).toBe('settled');
  }, 60000);

  it('refuses a bank value outside the bounds, compared by length, not as text', () => {
    const cs = env().store.configurationSet('example-bank');
    const bad = { ...cs, values: [...cs.values, { parameter: `${RETURN}#replyWait`, value: 'P100D', validFrom: '2026-11-22' }, { parameter: `${RETURN}#uploadHold`, value: 'PT11M', validFrom: '2026-11-22' }] };
    const msgs = checkDocument(env(), bad, 'example-bank').filter((i) => i.code === 'L14').map((i) => i.message);
    expect(msgs).toEqual([expect.stringMatching(/replyWait = "P100D" is outside its bounds/), expect.stringMatching(/uploadHold = "PT11M" is outside its bounds/)]);
    expect(checkDocument(env(), cs, 'example-bank').filter((i) => i.code === 'L14')).toEqual([]);
  });
});

describe('the acknowledgement of the returns file (I-41, item 1)', () => {
  it('names the family of the file each WPS acknowledgement answers, and each await takes only its own', () => {
    const scheme = env().store.scheme('uae.wps');
    expect(scheme.messages.find((m: any) => m.id === 'uae.wps.ack').disposition.deliver.families).toEqual(['sif', 'rfr', 'prc', 'dif', 'rfa']);
    const families = Object.fromEntries(['submit-salary-file', 'request-refund', 'confirm-receipt', 'report-disbursement', 'return-credits'].map((n) => {
      const sub = env().store.doc(`uae.wps.sub.${n}.v1`);
      return [n, sub.do.find((s: any) => s.await?.message === 'uae.wps.ack')?.await.family];
    }));
    expect(families).toEqual({ 'submit-salary-file': 'sif', 'request-refund': 'rfr', 'confirm-receipt': 'prc', 'report-disbursement': 'dif', 'return-credits': 'rfa' });
  });
});
