import { describe, expect, it } from 'vitest';
import { bankClosedDays, businessCalendarIn } from '../src/core/reference.js';
import { parseYaml } from '../src/core/yaml.js';
import { exportPackage } from '../src/export/exporter.js';
import { buildConfigurationRelease, buildDistribution } from '../src/export/distribution.js';
import { Run } from '../src/interp/interpreter.js';
import { runTest } from '../src/testing/runner.js';
import { run as cutOff } from '../scripts/p.decide.cut-off.v1.js';
import { freshEnv } from './helpers.js';
import './support/signing-env.js';

// The bank's cut-off (R-12-28, Issues I-186 and I-191) and the bank's own closing days in the business calendar (R-09-102). The
// example bank's cut-off for a WPS salary file it sends is 14:00 in Dubai (UTC+4), after which the file counts for the next
// business day (b.ref.cut-offs.v1); 31 December 2026 is a half day closing at noon (b.ref.bank-calendar.v1); the market's
// non-business days are Saturday, Sunday and the fixed holidays of uae.p.ref.business-calendar.v1 (1 to 3 December 2026, 1 January
// 2027). Every value reaches the step through the bank's configuration, read from its reference tables (R-11-51). Synthetic.

const WPS_OUT = { cutOffs: [{ scheme: 'uae.wps', direction: 'outward', cutOff: '14:00', timeZone: 'Asia/Dubai', afterCutOff: 'next-business-day' }] };
const MARKET = [{ day: 'saturday' }, { day: 'sunday' }, { day: '2026-12-01' }, { day: '2026-12-02' }, { day: '2026-12-03' }, { day: '2027-01-01' }];
const header = (receivedAt: string, extra: any = {}) => ({ paymentId: 'P-1', scheme: 'uae.wps', direction: 'outward', receivedAt, ...extra });
async function step(receivedAt: string, params: any = {}, h: any = {}) {
  const ctx: any = { header: header(receivedAt, h) };
  const out = await cutOff({ ctx, params: { ...WPS_OUT, nonBusinessDays: MARKET, closingDays: [], earlyClosings: [], ...params }, clock: { now: () => receivedAt, today: () => receivedAt.slice(0, 10) } } as any);
  const outcome = typeof out === 'string' ? out : out.outcome;
  return { outcome, reasonCode: typeof out === 'string' ? undefined : out.reasonCode, fact: ctx.facts.cutOff, businessDate: ctx.header.businessDate };
}

describe('the cut-off step (p.decide.cut-off.v1, R-12-28)', () => {
  it('takes a file before the cut-off for its day, in the bank\'s time zone', async () => {
    const r = await step('2026-11-24T09:59:00.000Z'); // 13:59 in Dubai
    expect(r).toMatchObject({ outcome: 'taken', businessDate: undefined, fact: { applied: false, cutOff: '14:00', timeZone: 'Asia/Dubai', receivedLocal: '2026-11-24T13:59:00', businessDate: '2026-11-24' } });
    // At the cut-off itself the file is in time; a second after it is not.
    expect((await step('2026-11-24T10:00:00.000Z')).fact.applied).toBe(false);
    expect((await step('2026-11-24T10:00:01.000Z')).fact.applied).toBe(true);
  });

  it('moves a file after the cut-off to the next business day, over the weekend and the holidays', async () => {
    expect(await step('2026-11-24T11:00:00.000Z')).toMatchObject({ outcome: 'taken', businessDate: '2026-11-25', fact: { applied: true, movedFrom: '2026-11-24', businessDate: '2026-11-25', afterCutOff: 'next-business-day' } });
    // Friday after the cut-off: Monday. Monday 30 November after it: Friday 4 December, past the three days of National Day.
    expect((await step('2026-11-27T12:00:00.000Z')).businessDate).toBe('2026-11-30');
    expect((await step('2026-11-30T12:00:00.000Z')).businessDate).toBe('2026-12-04');
    // Late on 23:30 UTC is the next morning in Dubai: in time for that day.
    expect(await step('2026-11-24T23:30:00.000Z')).toMatchObject({ outcome: 'taken', fact: { applied: false, receivedLocal: '2026-11-25T03:30:00', businessDate: '2026-11-25' } });
  });

  it('takes a file of a day that is no business day for the next one, and never refuses it for that', async () => {
    const r = await step('2026-11-28T05:00:00.000Z', { cutOffs: [{ ...WPS_OUT.cutOffs[0], afterCutOff: 'refuse' }] });
    expect(r).toMatchObject({ outcome: 'taken', businessDate: '2026-11-30', fact: { applied: false, reason: 'accepted on a day that is no business day', movedFrom: '2026-11-28' } });
  });

  it('refuses a file after the cut-off when the bank\'s row says so (TM01)', async () => {
    const r = await step('2026-11-24T11:00:00.000Z', { cutOffs: [{ ...WPS_OUT.cutOffs[0], afterCutOff: 'refuse' }] });
    expect(r).toMatchObject({ outcome: 'refused', reasonCode: 'TM01', businessDate: undefined, fact: { applied: true, reasonCode: 'TM01' } });
  });

  it('cuts off earlier on the bank\'s half day, and skips the days the bank is closed', async () => {
    const half = { earlyClosings: [{ day: '2026-12-31', closesAt: '12:00' }] };
    expect(await step('2026-12-31T08:30:00.000Z', half)).toMatchObject({ outcome: 'taken', businessDate: '2027-01-04', fact: { applied: true, cutOff: '12:00', halfDay: true } });
    expect((await step('2026-12-31T07:30:00.000Z', half)).fact.applied).toBe(false);
    const closed = { closingDays: [{ day: '2026-11-25', kind: 'closed' }, { day: '2026-11-26', kind: 'half-day' }] };
    expect((await step('2026-11-24T11:00:00.000Z', closed)).businessDate).toBe('2026-11-26');
  });

  it('changes nothing without a row for the file\'s scheme and direction, or with no cut-offs at all', async () => {
    expect(await step('2026-11-24T11:00:00.000Z', {}, { direction: 'inward' })).toMatchObject({ outcome: 'taken', businessDate: undefined, fact: { applied: false } });
    expect(await step('2026-11-24T11:00:00.000Z', { cutOffs: [] })).toMatchObject({ outcome: 'taken', businessDate: undefined });
  });
});

describe('the bank\'s cut-off values come from its reference tables (R-11-51, R-12-28)', () => {
  it('reads the example bank\'s cut-offs, closing days and the market\'s calendar edition by edition', () => {
    const env = freshEnv();
    const it = new Run({ store: env.store, tenant: 'example-bank', evaluationDate: '2026-11-24' } as any);
    const decl = env.store.doc('p.decide.cut-off.v1').parameters;
    expect(it.lookupParam('p.decide.cut-off.v1', 'cutOffs', decl.cutOffs, '2026-11-24')).toEqual(WPS_OUT.cutOffs);
    expect(it.lookupParam('p.decide.cut-off.v1', 'earlyClosings', decl.earlyClosings, '2026-11-24')).toEqual([{ day: '2026-12-31', closesAt: '12:00' }]);
    expect(it.lookupParam('p.decide.cut-off.v1', 'closingDays', decl.closingDays, '2026-11-24')).toEqual([{ day: '2026-12-31', kind: 'half-day' }]);
    expect(it.lookupParam('p.decide.cut-off.v1', 'nonBusinessDays', decl.nonBusinessDays, '2026-11-24')).toEqual(MARKET);
    // Before the tables' first edition the defaults stand: no cut-off.
    expect(it.lookupParam('p.decide.cut-off.v1', 'cutOffs', decl.cutOffs, '2026-09-30')).toEqual([]);
  });

  it('the configuration release and every engine package carry the values read, never a reference to a table', () => {
    const env = freshEnv();
    const dist = buildDistribution(env, { bank: 'example-bank', engine: 'temporal', id: '2026.11.24-9' });
    const cfg = buildConfigurationRelease(env, { bank: 'example-bank', against: [{ releaseId: dist.releaseId, parameterContract: dist.manifest.parameterContract }] });
    const values = parseYaml(cfg.files['configuration/configuration-set.yaml']).values;
    expect(values.find((v: any) => v.parameter === 'p.decide.cut-off.v1#cutOffs')).toEqual({ parameter: 'p.decide.cut-off.v1#cutOffs', validFrom: '2026-10-01', value: WPS_OUT.cutOffs });
    // The package a deployed worker runs holds no reference table: its configuration holds the values (R-09-103).
    const pkg = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'temporal', tenant: 'example-bank' } as any);
    const packaged = parseYaml(pkg.files['configuration/example-bank.yaml']);
    expect(packaged.values.some((v: any) => v.fromReference)).toBe(false);
    expect(packaged.values.find((v: any) => v.parameter === 'p.decide.cut-off.v1#nonBusinessDays').value).toEqual(MARKET);
    expect(Object.keys(pkg.files).some((f) => f.includes('.ref.'))).toBe(false);
  });
});

describe('a salary file meets the cut-off end to end on the studio interpreter (R-12-28)', () => {
  const bench = [
    ...['core-banking', 'sanctions', 'fraud', 'channel', 'payment-record', 'facts-out', 'party-register'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
    { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'release' },
    { port: 'p.port.encryption.v1', simulator: 'p.sim.encryption.v1', scenario: 'accept' },
    { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
    { port: 'uae.wps.port.labour-ministry.v1', simulator: 'uae.wps.sim.labour-ministry.v1', scenario: 'accept' },
    { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
    { port: 'b.port.treasury.v1', simulator: 'b.sim.treasury.v1', scenario: 'accept' },
    { port: 'b.port.reference-data.v1', simulator: 'b.sim.reference-data.v1', scenario: 'accept' },
  ];
  const file = (id: string) => ({
    kind: 'test', id, title: 'A salary file of the example bank', target: 'p.wf.corporate.v1', level: 'end-to-end', evaluationDate: '2026-11-24',
    tenant: 'example-bank', bench, given: { inbound: 'samples/wps-sif-valid.sif', message: 'uae.wps.sif' }, expect: {},
  });
  const leg = (r: any) => r.record!.instances.find((i: any) => i.workflow === 'uae.wps.wf.salary-file-out.v1');

  it('before the cut-off: the business date is the day it arrived', async () => {
    const env = freshEnv();
    const r = await runTest(env, file('CUT-OFF-IN-TIME'));
    expect(r.error).toBeUndefined();
    expect(r.trace.filter((t) => t.startsWith('leg:') || t.startsWith('end:'))).toEqual(['leg:settled', 'end:settled']);
    expect(leg(r).context.facts.cutOff).toMatchObject({ applied: false, cutOff: '14:00', receivedLocal: '2026-11-24T10:00:00', businessDate: '2026-11-24' });
  }, 60000);

  it('after the cut-off: it counts for the next business day; refused when the bank says so', async () => {
    const env = freshEnv();
    const r = await runTest(env, file('CUT-OFF-LATE'), { startTime: '11:00:00' } as any);
    expect(r.outcome).toBe('settled');
    expect(leg(r).context.header.businessDate).toBe('2026-11-25');
    expect(leg(r).context.facts.cutOff).toMatchObject({ applied: true, movedFrom: '2026-11-24', businessDate: '2026-11-25' });
    const refuse = [{ parameter: 'p.decide.cut-off.v1#cutOffs', value: [{ ...WPS_OUT.cutOffs[0], afterCutOff: 'refuse' }], validFrom: '2026-11-01' }];
    const x = await runTest(env, file('CUT-OFF-REFUSED'), { startTime: '11:00:00', values: refuse } as any);
    expect(x.outcome).toBe('rejected');
    expect(leg(x).context.facts.cutOff).toMatchObject({ applied: true, reasonCode: 'TM01' });
  }, 60000);
});

describe('the bank\'s own closed days are no business days (R-09-102)', () => {
  it('adds them to the market\'s calendar; a half day stays a business day', () => {
    const env = freshEnv();
    const t = env.store.doc('b.ref.bank-calendar.v1', undefined, 'example-bank');
    const market = env.store.doc('uae.p.ref.business-calendar.v1');
    const own = { ...t, editions: [...t.editions, { validFrom: '2026-11-20', rows: [{ day: '2026-11-26', kind: 'closed', name: 'Synthetic closing day' }, ...t.editions[0].rows] }] };
    expect(bankClosedDays([own], '2026-11-24')).toEqual(['2026-11-26']);
    expect(bankClosedDays([own], '2026-11-19')).toEqual([]);
    const cal = businessCalendarIn([market, own], '2026-11-24')!;
    expect(cal.weekend).toEqual([0, 6]);
    expect(cal.holidays).toEqual(['2026-11-26', '2026-12-01', '2026-12-02', '2026-12-03', '2027-01-01']);
    expect(cal.table).toBe('uae.p.ref.business-calendar.v1+b.ref.bank-calendar.v1');
    expect(businessCalendarIn([market, t], '2026-11-24')!.holidays).not.toContain('2026-12-31');
  });
});
