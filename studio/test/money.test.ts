import { describe, expect, it } from 'vitest';
import { Decimal, moneyHelpers, significantDigits } from '../src/core/decimal.js';
import { evalExpr } from '../src/rules/expr.js';
import { runRuleSet, runTable } from '../src/rules/service.js';
import { runScript } from '../src/interp/sandbox.js';
import { runPythonScript } from '../src/interp/pysandbox.js';
import { freshEnv } from './helpers.js';
import { moneyIssues } from '../src/checks/checks.js';

// Money is exact everywhere (R-02-17, R-04-23; Issue I-96).

// Seven lines whose binary floating-point sum is 7894.870000000001, and whose decimal sum is 7894.87.
const LINES = [0.1, 0.2, 0.05, 0.2, 2697.81, 1395.44, 3801.07];

describe('the decimal module', () => {
  it('reads a number by its text and a decimal string exactly', () => {
    expect(Decimal.parse(0.1)!.toString()).toBe('0.1');
    expect(Decimal.parse('90071992547409.93')!.toString()).toBe('90071992547409.93');
    expect(Decimal.parse('1.50')!.toString()).toBe('1.5');
    expect(Decimal.parse(1e-7)!.toString()).toBe('0.0000001');
    expect(Decimal.parse(-0)!.toString()).toBe('0');
    expect(Decimal.parse('abc')).toBeUndefined();
    expect(Decimal.parse(NaN)).toBeUndefined();
  });
  it('adds, multiplies, divides and rounds half away from zero', () => {
    const d = (x: any) => Decimal.parse(x)!;
    expect(LINES.reduce((a, x) => a + x, 0)).not.toBe(7894.87); // the float sum the factory rule saw
    expect(LINES.reduce((a, x) => a.plus(d(x)), Decimal.ZERO).toString()).toBe('7894.87');
    expect(d('90071992547409.93').times(d(100)).toString()).toBe('9007199254740993');
    expect(d(1).div(d(3))!.toString()).toBe('0.33333333333333333333');
    expect(d(2).div(d(3))!.toString()).toBe('0.66666666666666666667');
    expect(d(1).div(d(0))).toBeUndefined();
    expect(d('-7.5').mod(d(2))!.toString()).toBe('-1.5');
    expect(d('0.125').round(2).toString()).toBe('0.13');
    expect(d('-0.125').round(2).toString()).toBe('-0.13');
    expect(d('2.675').round(2).toString()).toBe('2.68');
    expect(d('1500.005').round(2).toString()).toBe('1500.01');
  });
  it('gives a number up to 15 significant digits and a decimal string beyond', () => {
    expect(Decimal.parse('7894.87')!.toValue()).toBe(7894.87);
    expect(JSON.stringify(Decimal.parse('9007199254740993')!.toValue())).toBe('"9007199254740993"');
    expect(significantDigits(123456789012345)).toBe(15);
    expect(significantDigits('1234567890123456.7')).toBe(17);
    expect(significantDigits(0.000001)).toBe(1);
  });
});

describe('the rule language computes on exact decimals (R-04-23)', () => {
  it('sums, compares and rounds as decimals', () => {
    const items = LINES.map((v) => ({ v }));
    expect(evalExpr('sum(items, i => i.v)', { items })).toBe(7894.87);
    expect(evalExpr('sum(items, i => i.v) == 7894.87', { items })).toBe(true);
    expect(evalExpr('sumWhere(items, i => i.v < 1, i => i.v)', { items })).toBe(0.55);
    expect(evalExpr('0.1 + 0.2 == 0.3 and 0.3 - 0.1 == 0.2 and 1.1 * 1.1 == 1.21', {})).toBe(true);
    expect(evalExpr('round2(0.05 * 2.5)', {})).toBe(0.13);
    expect(evalExpr('round2(-0.05 * 2.5)', {})).toBe(-0.13);
    expect(evalExpr('round2(0.05 * 53.5)', {})).toBe(2.68);
    expect(evalExpr('max(items, i => i.v)', { items })).toBe(3801.07);
    expect(evalExpr('min(items, i => i.v)', { items })).toBe(0.05);
  });
  it('carries an amount beyond 15 digits as a decimal string, and compares it exactly', () => {
    const p = { a: '90071992547409.93' };
    expect(evalExpr('p.a * 100 == 9007199254740993', { p })).toBe(true);
    expect(evalExpr('p.a * 100 == 9007199254740992', { p })).toBe(false);
    expect(evalExpr("p.a + 0.01 == '90071992547409.94'", { p })).toBe(true);
    expect(JSON.stringify(evalExpr('p.a + 0.01', { p }))).toBe('"90071992547409.94"');
    expect(String(evalExpr("p.a + '0.01'", { p }))).toBe('90071992547409.94');
    expect(evalExpr("'AE' + '07'", {})).toBe('AE07');
  });
  it('accepts a correct file total that binary floating point refuses (Issue I-96)', () => {
    const env = freshEnv();
    const resolve = (n: string) => env.store.doc(n);
    const items = LINES.map((v) => ({ batchId: 'F-1', fixedPay: v, variablePay: 0 }));
    const facts = { Batch: [{ id: 'F-1', currency: 'AED', numberOfTransactions: LINES.length, controlSum: 7894.87, debtorAgent: { clearingMemberId: '302620122' } }],
      Item: items, WpsBatch: [{ employerId: '1234567890123' }] };
    const res = runRuleSet('uae.wps.ruleset.salary-file.v1', facts, { resolve, evaluationDate: '2026-11-17' });
    expect(res.findings.map((f) => f.reasonCode)).not.toContain('AM10');
    const short = runRuleSet('uae.wps.ruleset.salary-file.v1', { ...facts, Batch: [{ ...facts.Batch[0], controlSum: 7894.86 }] }, { resolve, evaluationDate: '2026-11-17' });
    expect(short.findings.map((f) => f.reasonCode)).toContain('AM10');
  });
  it('compares table bounds exactly', () => {
    const t = { kind: 'table', name: 'b.table.t.v1', release: '1.0.0', hitPolicy: 'first', inputs: [{ name: 'x', expression: 'payment.v * 100' }],
      rows: [{ when: { x: { gte: '9007199254740993' } }, then: { band: 'high' } }, { when: {}, then: { band: 'low' } }] };
    const r = (v: any) => runTable(t, { Payment: [{ v }] }, { resolve: () => undefined, evaluationDate: '2026-11-17' }).decisions[0].value;
    expect(r('90071992547409.93')).toBe('high');
    expect(r('90071992547409.92')).toBe('low');
  });
});

describe('scripts get the same arithmetic on rules.money', () => {
  const api = { ports: {}, rules: { run: () => ({}), money: moneyHelpers }, params: {}, clock: { now: () => '2026-11-17T06:00:00.000Z', today: () => '2026-11-17' }, ids: { next: () => 'id-1' }, log: { info: () => {} } };
  const view = () => ({ items: LINES.map((v) => ({ v })), facts: {} });
  it('in TypeScript', async () => {
    const src = `export async function run({ ctx, rules }) { ctx.facts.m = { total: rules.money.sum(ctx.items, (i) => i.v), big: rules.money.mul('90071992547409.93', 100), r: rules.money.round2(-0.125) }; return 'ok'; }`;
    const r = await runScript(src, 'm.ts', view(), ['items', 'facts'], ['facts'], api as any);
    expect(r.ctx.facts.m).toEqual({ total: 7894.87, big: '9007199254740993', r: -0.13 });
  });
  it('in Python, with the same results', async () => {
    const src = `def run(api):\n    m = api.rules.money\n    api.ctx['facts'] = {'m': {'total': m.sum(api.ctx['items'], lambda i: i['v']), 'big': m.mul('90071992547409.93', 100), 'r': m.round2(-0.125)}}\n    return 'ok'\n`;
    const r = await runPythonScript(src, 'm.py', view(), ['items', 'facts'], ['facts'], api as any);
    expect(r.ctx.facts.m).toEqual({ total: 7894.87, big: '9007199254740993', r: -0.13 });
  });
});

describe('lint L40 (R-02-17, R-04-23)', () => {
  const rule = (having: string, given: any = { Batch: { controlSum: 300 } }) => ({ kind: 'rule', name: 'b.rule.t.v1', when: [{ match: { fact: 'Batch', as: 'b' } }, { having }], examples: [{ name: 'x', given }] });
  it('refuses a floating-point tolerance', () => {
    expect(moneyIssues(rule('b.total - b.controlSum > 0.001'))).toHaveLength(1);
    expect(moneyIssues(rule('0.01 >= round2(b.total - b.controlSum)'))).toHaveLength(1);
    expect(moneyIssues(rule('b.total != b.controlSum'))).toEqual([]);
    expect(moneyIssues(rule('b.amount < 0.1'))).toEqual([]);
  });
  it('refuses a number a binary double cannot hold, and takes the same amount as a string', () => {
    expect(moneyIssues(rule('b.total != b.controlSum', { Batch: { controlSum: 1234567890123456.78 } }))).toHaveLength(1);
    expect(moneyIssues(rule('b.total != b.controlSum', { Batch: { controlSum: '1234567890123456.78' } }))).toEqual([]);
    expect(moneyIssues({ kind: 'configuration-set', values: [{ value: 12345678901234567 }] })).toHaveLength(1);
  });
});
