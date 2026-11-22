import { describe, expect, it } from 'vitest';
import { Checker } from '../src/checks/checks.js';
import { runWorkflow } from '../src/interp/interpreter.js';
import { baseConfig, runTest } from '../src/testing/runner.js';
import { clone, freshEnv } from './helpers.js';

// The refund order (R-12-19 to R-12-22, Issue I-133): a refund is an order of the platform's p.wf.refund.v1, and a scheme is
// only its leg. The platform finds the earlier payment, converts and prices each line on the bank's values, blocks, holds for
// approval and posts; the WPS legs send and acknowledge the DCR and the RRR. A second scheme fills the same slots with no change
// to the platform workflow.

const REFUND = 'p.wf.refund.v1';
const ORDER = {
  header: { paymentId: 'R-1', kind: 'refund', direction: 'inward', scheme: 'uae.demo', originalPaymentId: 'P-0' },
  batch: { id: 'R-1', numberOfTransactions: 2, controlSum: 600, currency: 'AED' },
  items: [
    { payment: { instructedAmount: { value: 100, currency: 'AED' }, debtorAccount: { iban: 'AE070331234567890123456' } } },
    { payment: { instructedAmount: { value: 500, currency: 'AED' }, debtorAccount: { iban: 'AE970336543210987654321' } } },
  ],
};
const BENCH = [
  ['p.port.core-banking.v1', 'p.sim.core-banking.v1'], ['p.port.payment-record.v1', 'p.sim.payment-record.v1'], ['p.port.channel.v1', 'p.sim.channel.v1'],
  ['p.port.facts-out.v1', 'p.sim.facts-out.v1'],
].map(([port, simulator]) => ({ port, simulator, scenario: 'accept' })).concat([{ port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'approve' }]);

// `uae.demo` is a synthetic scheme made up for this test. It stands for no real scheme: its legs answer with nothing but a
// notification, so the order's whole lifecycle is the platform's.
const prov = { origin: 'authored', status: 'provisional', note: 'Synthetic, for a test.' };
const DEMO_LEG = {
  kind: 'workflow', wfKind: 'leg', name: 'uae.demo.wf.refund-answer.v1', release: '1.0.0', dsl: '1.0.0', title: 'Demo refund answered', direction: 'inward',
  implements: { slot: 'scheme-leg', of: [REFUND] }, entry: { reads: ['header', 'facts.posting'] }, exit: { writes: ['status'], outcomes: ['settled', 'rejected', 'pending'] },
  do: [{ id: 'tell', call: 'p.notify.channel.v1' }, { end: 'settled' }], provenance: prov,
};
const DEMO_REFUSE = {
  ...DEMO_LEG, name: 'uae.demo.wf.refund-refused.v1', title: 'Demo refund refused', implements: { slot: 'scheme-refuse', of: [REFUND] },
  entry: { reads: ['header', 'facts.approval'] }, exit: { writes: ['status'], outcomes: ['refused', 'pending'] }, do: [{ id: 'tell', call: 'p.notify.channel.v1' }, { end: 'refused' }],
};

describe('a refund order on the platform, with WPS as its leg (R-12-19, R-12-21)', () => {
  it('posts on the platform; the WPS leg only returns the funds and answers', async () => {
    const env = freshEnv();
    const r = await runTest(env, env.store.test('WPS-RRI-002')!.doc);
    expect(r.passed, JSON.stringify(r.checks?.filter((c: any) => !c.passed))).toBe(true);
    const platform = r.record!.instances.find((i) => i.workflow === REFUND)!;
    const leg = r.record!.instances.find((i) => i.workflow === 'uae.wps.wf.refund-request-in.v1')!;
    expect(platform.trace).toEqual(expect.arrayContaining(['reserve:reserved', 'approval:approved', 'post:posted', 'leg:settled']));
    expect(leg.trace.some((t: string) => /^(post|reserve|credit|debit)/.test(t))).toBe(false);
    // A new order that names the earlier payment file by its id (R-02-20).
    expect(platform.context.header).toMatchObject({ kind: 'refund', originalPaymentId: '000000000000000000000001' });
    expect(platform.context.facts.fx).toMatchObject({ outcome: 'converted', rate: 1, currency: 'AED', amount: 600 });
    expect(platform.context.facts.charges).toMatchObject({ outcome: 'priced', amount: 10.6, totalChargesAndTaxAmount: 11.14 });
  });

  it('is refused by the refuse leg when the approver declines, the block released and nothing posted', async () => {
    const env = freshEnv();
    const r = await runTest(env, env.store.test('WPS-RRI-003')!.doc);
    expect(r.passed, JSON.stringify(r.checks?.filter((c: any) => !c.passed))).toBe(true);
    expect(r.trace).not.toContain('post:posted');
    expect(r.record!.instances.map((i) => i.workflow)).toContain('uae.wps.wf.refund-request-refuse.v1');
  });

  it('parks a failed posting for an operator instead of ending the order (R-05-55, Issue I-125)', () => {
    const env = freshEnv();
    const wf = env.store.doc(REFUND)!;
    const post = wf.do.find((s: any) => s.id === 'post');
    expect(post.policy.park).toBe('P1D');
    expect(env.store.doc('p.post.accounting.v1')!.idempotencyKey).toBeTruthy();
  });
});

describe('FX and charges are the platform\'s, on the bank\'s values, in exact decimals (R-12-20)', () => {
  it('converts each line at the bank\'s rate and rounds each line half away from zero', async () => {
    const env = freshEnv();
    const r = await runTest(env, env.store.test('P-FX-002')!.doc);
    expect(r.passed).toBe(true);
    const ctx = r.record!.instances[0].context;
    expect(ctx.items.map((i: any) => i.facts.fx.amount)).toEqual([27.23, 0.01]);
    expect(ctx.items.map((i: any) => i.facts.fx.source)).toEqual(['bank', 'bank']);
    expect(ctx.facts.fx).toMatchObject({ amount: 27.24, rate: 0.2723, currency: 'USD', lines: 2 });
  });

  it('prices each line on its slab, with the floor, the ceiling and VAT per line', async () => {
    const env = freshEnv();
    const r = await runTest(env, env.store.test('P-CHG-001')!.doc);
    expect(r.passed).toBe(true);
    const items = r.record!.instances[0].context.items;
    expect(items.map((i: any) => [i.facts.charges.amount, i.facts.charges.tax.amount, i.facts.charges.totalChargesAndTaxAmount])).toEqual([[5.1, 0.26, 5.36], [5.5, 0.28, 5.78]]);
  });

  it('takes a rate and a slab from the bank\'s configuration by date, not from any pack', async () => {
    const env = freshEnv();
    const t = clone(env.store.test('P-CHG-001')!.doc);
    // From 2027 the bank charges a flat 20 on every line up to a million: a value, no new release.
    const values = [{ parameter: 'p.enrich.charges.v1#chargeSlabs', value: [{ upTo: 1000000, fixed: 20, percent: 0 }], validFrom: '2027-01-01' }];
    const before = await runTest(env, t, { values });
    expect(before.record!.instances[0].context.facts.charges.amount).toBe(10.6);
    const after = await runTest(env, { ...t, evaluationDate: '2027-01-02', expect: { outcome: 'priced' } }, { values });
    expect(after.record!.instances[0].context.items.map((i: any) => i.facts.charges.amount)).toEqual([20, 20]);
    // No scheme pack declares a rate or a charge: they are the platform's dials only.
    const dials = env.store.byKind('activity').filter((e) => Object.keys(e.doc.parameters ?? {}).some((k) => ['rates', 'chargeSlabs'].includes(k)));
    expect([...new Set(dials.map((e) => e.doc.name.split('.')[0]))]).toEqual(['p']);
  });

  it('keeps every digit of an amount beyond fifteen significant digits (CK-MONEY-01)', async () => {
    const env = freshEnv();
    const t = clone(env.store.test('P-FX-001')!.doc);
    t.given.context.items = [{ payment: { instructedAmount: { value: '12345678901234.56', currency: 'AED' }, debtorAccount: { iban: 'AE070331234567890123456' } } }];
    const r = await runTest(env, { ...t, expect: { outcome: 'converted' } });
    expect(String(r.record!.instances[0].context.facts.fx.amount)).toBe('12345678901234.56');
  });
});

describe('a second scheme fills the refund order\'s slots without a change to the platform workflow (R-12-19)', () => {
  it('runs the same p.wf.refund.v1 with a synthetic scheme\'s legs', async () => {
    const env = freshEnv();
    const platform = clone(env.store.doc(REFUND));
    const cfg = baseConfig(env, { tenant: 'example-bank', evaluationDate: '2026-11-17', bench: BENCH }, {
      documents: { [DEMO_LEG.name]: DEMO_LEG, [DEMO_REFUSE.name]: DEMO_REFUSE },
    });
    const r = await runWorkflow(cfg, REFUND, clone(ORDER), { legs: { 'scheme-leg': DEMO_LEG.name, 'scheme-refuse': DEMO_REFUSE.name } });
    expect(r.root!.outcome).toBe('settled');
    expect(r.root!.trace).toEqual(['original:found', 'fx:converted', 'charges:priced', 'reserve:reserved', 'approval:approved', 'post:posted', 'verify:verified', 'leg:settled', 'finally:notify', 'finally:publish', 'end:settled']);
    expect(r.run.allInstances().map((i) => i.doc.name)).toContain(DEMO_LEG.name);
    expect(env.store.doc(REFUND)).toEqual(platform);
    // Declined, the same platform workflow hands the answer to the other scheme's refuse leg.
    const declined = baseConfig(env, { tenant: 'example-bank', evaluationDate: '2026-11-17', bench: BENCH.map((b) => b.port === 'p.port.operations-queue.v1' ? { ...b, scenario: 'decline' } : b) }, {
      documents: { [DEMO_LEG.name]: DEMO_LEG, [DEMO_REFUSE.name]: DEMO_REFUSE },
    });
    const d = await runWorkflow(declined, REFUND, clone(ORDER), { legs: { 'scheme-leg': DEMO_LEG.name, 'scheme-refuse': DEMO_REFUSE.name } });
    expect(d.root!.outcome).toBe('rejected');
    expect(d.root!.trace).toContain('refuse-declined:refused');
  });

  it('passes the checks; the same leg that posts is refused by L47 (mutation M66)', () => {
    const env = freshEnv();
    const docs = [...env.store.current().map((e) => clone(e.doc)), clone(DEMO_LEG), clone(DEMO_REFUSE)];
    const ours = (issues: any[]) => issues.filter((i) => i.severity === 'error' && [DEMO_LEG.name, DEMO_REFUSE.name].includes(i.key));
    expect(ours(new Checker(docs, []).checkAll())).toEqual([]);
    const posting = { ...clone(DEMO_LEG), do: [{ id: 'debit', call: 'p.post.accounting.v1', handle: { rejected: { end: 'rejected' }, unknown: { end: 'pending' } } }, { end: 'settled' }] };
    const bad = new Checker([...docs.filter((x) => x.name !== DEMO_LEG.name), posting], []).checkAll().filter((i) => i.code === 'L47');
    expect(bad.map((i) => i.message)).toEqual([`Step debit calls p.post.accounting.v1, but ${REFUND}, whose slot it fills, owns the order's blocks and postings`]);
  });
});
