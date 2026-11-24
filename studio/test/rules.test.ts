import { describe, expect, it } from 'vitest';
import { evalExpr, ibanValid, parse } from '../src/rules/expr.js';
import { analyseTable, runExamples, runRuleSet, runTable } from '../src/rules/service.js';
import { freshEnv } from './helpers.js';

describe('the expression language (C06-S1)', () => {
  it('evaluates paths, comparisons, functions and lambdas', () => {
    const env = { p: { a: { v: 5 } }, items: [{ x: 1, y: 2 }, { x: 3, y: 4 }], list: ['a', 'b'] };
    expect(evalExpr('p.a.v > 4 and not isBlank(p.a.v)', env)).toBe(true);
    expect(evalExpr('sum(items, i => i.x + i.y)', env)).toBe(10);
    expect(evalExpr("in('b', list)", env)).toBe(true);
    expect(evalExpr("not in('c', list)", env)).toBe(true);
    expect(evalExpr("'b' in list", env)).toBe(true);
    expect(evalExpr("matches('302620122', '^[0-9]{9}$')", env)).toBe(true);
    expect(evalExpr('p.missing.deep == null', env)).toBe(true);
    expect(evalExpr('0.1 + 0.2 == 0.3', env)).toBe(true);
  });
  it('refuses what it cannot parse', () => {
    expect(() => parse('a ==')).toThrow();
    expect(() => evalExpr('nope(1)', {})).toThrow(/Unknown function/);
  });
  it('checks IBAN check digits', () => {
    expect(ibanValid('AE070331234567890123456')).toBe(true);
    expect(ibanValid('AE080331234567890123456')).toBe(false);
  });
});

describe('rule sessions (C06-S2 to S5)', () => {
  const env = freshEnv();
  const resolve = (n: string) => env.store.doc(n);
  it('runs every example that every rule and table carries', () => {
    let n = 0;
    for (const e of env.store.current().filter((x) => x.kind === 'rule' || x.kind === 'table')) {
      for (const r of runExamples(e.doc, resolve)) {
        expect(r.passed, `${e.name} ${r.name} ${JSON.stringify(r.actual)}`).toBe(true);
        n++;
      }
    }
    expect(n).toBe(74); // 1 for the outward refund order (workflow-select 1.2.0, I-136); 1 for the refund order (workflow-select 1.1.0, I-133); 4 for the suspended employer (Issue I-88); 2 for the participant directory (Issue I-115)
  });
  it('stops at a stage with a blocking finding (R-04-14)', () => {
    // a wrong currency (field stage) and a wrong control total (cross-field stage): the cross-field rule never runs
    const facts = { Batch: [{ id: 'F-1', currency: 'USD', numberOfTransactions: 1, controlSum: 999, debtorAgent: { clearingMemberId: '302620122' } }],
      Item: [{ batchId: 'F-1', payment: { instructedAmount: { value: 1, currency: 'USD' } } }], WpsBatch: [{ employerId: '1234567890123' }] };
    const res = runRuleSet('uae.wps.ruleset.salary-file.v1', facts, { resolve, evaluationDate: '2026-11-17' });
    expect(res.stoppedAtStage).toBe('field');
    expect(res.findings.map((f) => f.reasonCode)).toContain('AM03');
    expect(res.findings.map((f) => f.reasonCode)).not.toContain('AM10');
    expect(res.worst).toBe('reject');
  });
  it('reads a parameter value by date (R-01-17)', () => {
    const lookup = (_o: string, p: string, decl: any, date: string) => (p === 'internalLimit' && date >= '2027-01-01' ? 100000 : decl.default);
    const facts = { Payment: [{ instructedAmount: { value: 150000, currency: 'AED' } }] };
    expect(runRuleSet('b.ruleset.internal-limit.v1', facts, { resolve, lookup, evaluationDate: '2026-12-31' }).worst).toBe('pass');
    expect(runRuleSet('b.ruleset.internal-limit.v1', facts, { resolve, lookup, evaluationDate: '2027-01-01' }).worst).toBe('reject');
  });
  it('reads a parameter its rule declares payment-date as of the payment\'s business date, and every other as of the step (R-05-93, I-236)', () => {
    const set = env.store.doc('b.ruleset.internal-limit.v1');
    const ruleName = (set.rules ?? [])[0];
    const rule = env.store.doc(ruleName);
    const asOfPayment = { ...rule, parameters: Object.fromEntries(Object.entries<any>(rule.parameters).map(([k, d]) => [k, { ...d, lookupDate: 'payment-date' }])) };
    const resolved = (n: string) => (n === ruleName ? asOfPayment : resolve(n));
    const lookup = (_o: string, p: string, decl: any, date: string) => (p === 'internalLimit' && date >= '2027-01-01' ? 100000 : decl.default);
    const facts = { Payment: [{ instructedAmount: { value: 150000, currency: 'AED' } }] };
    // The step is in 2027, the payment's business date in 2026: payment-date reads the 2026 value, step-time the 2027 one.
    expect(runRuleSet(set.name, facts, { resolve: resolved, lookup, evaluationDate: '2027-01-02', businessDate: '2026-12-31' }).worst).toBe('pass');
    expect(runRuleSet(set.name, facts, { resolve, lookup, evaluationDate: '2027-01-02', businessDate: '2026-12-31' }).worst).toBe('reject');
    // A rule with no business date to take reads as of the step.
    expect(runRuleSet(set.name, facts, { resolve: resolved, lookup, evaluationDate: '2027-01-02' }).worst).toBe('reject');
    // The word the schema does not have is not read as payment-date.
    const business = { ...rule, parameters: Object.fromEntries(Object.entries<any>(rule.parameters).map(([k, d]) => [k, { ...d, lookupDate: 'business' }])) };
    expect(runRuleSet(set.name, facts, { resolve: (n) => (n === ruleName ? business : resolve(n)), lookup, evaluationDate: '2027-01-02', businessDate: '2026-12-31' }).worst).toBe('reject');
  });
  it('takes the first matching row and resolves an output that names an input', () => {
    const t = env.store.doc('p.table.rail-choice.v1');
    const res = runTable(t, { Header: [{ scheme: 'uae.wps' }] }, { resolve, evaluationDate: '2026-11-17' }, { enabledRails: ['uae.wps'] });
    expect(res.decisions).toEqual([{ rule: 'p.table.rail-choice.v1', name: 'rail', value: 'uae.wps', reason: 'The requested rail is enabled for this bank' }]);
  });
  it('reports gaps and overlaps in a unique table (R-04-18)', () => {
    const t = env.store.doc('p.table.workflow-select.v1');
    const rep = analyseTable(t);
    expect(rep.overlaps).toEqual([]);
    expect(rep.gaps.length).toBeGreaterThan(0);
    const broken = { ...t, rows: [...t.rows, { when: { kind: 'request' }, then: { workflow: 'x' } }] };
    expect(analyseTable(broken).overlaps.length).toBeGreaterThan(0);
  });
  it('keeps a trace of the rules fired (C06-S6)', () => {
    const res = runRuleSet('p.ruleset.canonical.v1', { Payment: [{ instructedAmount: { value: -1, currency: 'AED' } }], Header: [{ endToEndId: 'E' }] }, { resolve, evaluationDate: '2026-11-17' });
    expect(res.trace.find((t) => t.rule === 'p.rule.amount-positive.v1')?.fired).toBe(true);
  });
  it('refuses a file that names no currency on its batch or on a line (R-09-231, Issue I-294)', () => {
    const facts = (batch: any, lines: any[]) => ({ Header: [{ endToEndId: 'E' }], Batch: [{ id: 'B1', ...batch }], Item: lines.map((l) => ({ batchId: 'B1', payment: { instructedAmount: { value: 10, ...l } } })) });
    const codes = (f: any) => runRuleSet('p.ruleset.canonical.v1', f, { resolve, evaluationDate: '2026-11-17' }).findings.map((x: any) => x.reasonCode);
    expect(codes(facts({}, [{}, { currency: 'AED' }]))).toEqual(['AM03']);
    expect(codes(facts({ currency: 'AED' }, [{}, {}]))).toEqual([]);
    expect(codes(facts({}, [{ currency: 'AED' }]))).toEqual([]);
    expect(env.store.doc('p.validate.canonical.v2').reads).toEqual(['header', 'payment', 'batch', 'items[]']);
    expect(env.store.doc('p.seg.check-and-screen.v1').do[0].call).toBe('p.validate.canonical.v2');
  });
});
