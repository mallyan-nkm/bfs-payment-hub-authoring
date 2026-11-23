import { describe, expect, it } from 'vitest';
import { checkAll, checkDocument } from '../src/checks/checks.js';
import { buildPack, compareManifest } from '../src/governance/packs.js';
import * as gov from '../src/governance/governance.js';
import { runTest } from '../src/testing/runner.js';
import type { StudioEnv } from '../src/env.js';
import { freshEnv } from './helpers.js';

// ADIB's folder on the content branch, content-configurations/banks/adib/ (Issue I-86). By the owner's decision of
// 2026-11-22 the bank runs WPS: its folder carries the WPS values the source holds for it (README.md), binds the labour
// ministry notification that its WPS workflow calls, and keeps the employer AML check built and switched off, as DIB's
// folder does with a hook no live flow calls. Its salary files are encrypted for the Central Bank before they are sent
// (Issue I-87), and an employer the labour ministry has suspended is refused (Issue I-88).

const TENANT = 'adib';
const AML = 'b.hook.employer-aml-check.v1';
const NOTIFY = 'b.hook.labour-ministry-notify.v1';
let shared: StudioEnv | undefined;
const env = () => (shared ??= freshEnv({ banks: [TENANT] }));
const own = (e: StudioEnv) => e.store.all().filter((x) => x.tenant === TENANT);

const bench = (aml: string, ministry: string, register = 'accept', encryption = 'accept') => [
  ...['core-banking', 'sanctions', 'fraud', 'channel', 'payment-record', 'facts-out'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'release' },
  { port: 'p.port.encryption.v1', simulator: 'p.sim.encryption.v1', scenario: encryption },
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.wps.port.labour-ministry.v1', simulator: 'uae.wps.sim.labour-ministry.v1', scenario: register },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
  { port: 'b.port.aml-screening.v1', simulator: 'b.sim.aml-screening.v1', scenario: aml },
  { port: 'b.port.labour-ministry.v1', simulator: 'b.sim.labour-ministry.v1', scenario: ministry },
];

/** A synthetic salary file from the employer's channel, end to end through p.wf.corporate.v1, for the bank. */
const salaryFile = (outcome: string, aml = 'accept', ministry = 'accept', register = 'accept', encryption = 'accept') => ({
  kind: 'test', id: `ADIB-WPS-${aml}-${ministry}-${register}-${encryption}`, title: 'A salary file for the bank', target: 'p.wf.corporate.v1', level: 'end-to-end',
  evaluationDate: '2026-11-24', tenant: TENANT, bench: bench(aml, ministry, register, encryption), given: { inbound: 'samples/wps-sif-valid.sif', message: 'uae.wps.sif' },
  expect: { outcome },
});

const AML_ON = [
  { hookPoint: 'p.seg.check-and-screen.v1#after-validate', hook: AML, release: '1.0.0', validFrom: '2026-11-24' },
  { hookPoint: 'p.wf.corporate.v1#on-close', hook: NOTIFY, release: '1.0.0', validFrom: '2026-11-22' },
];

describe('the bank pack in content-configurations/banks/adib (WPS, Issue I-86)', () => {
  it('loads as its tenant, only when asked, and leaves the runtime formats out', () => {
    const e = env();
    expect(e.store.bankPacks.get(TENANT)).toBe(TENANT);
    const kinds = own(e).map((x) => x.kind);
    expect(kinds).toEqual(expect.arrayContaining(['pack', 'configuration-set', 'hook', 'activity', 'port', 'simulator', 'test']));
    expect(kinds).not.toContain('port-bindings');
    expect(own(e).every((x) => x.scope === 'b' || x.kind === 'test')).toBe(true);
    expect(freshEnv().store.tenants()).not.toContain(TENANT);
  });

  it('passes the schemas and the static checks, and sets the WPS values the source holds, inside their bounds', () => {
    const e = env();
    for (const x of own(e)) expect(e.schemas.validate(x.doc), x.key).toEqual([]);
    const names = new Set(own(e).map((x) => x.name));
    expect(checkAll(e).filter((i) => i.severity === 'error')).toEqual([]);
    expect(checkAll(e).filter((i) => names.has(i.key))).toEqual([]);
    const cs = e.store.configurationSet(TENANT);
    expect(gov.checkConfiguration(e, TENANT, cs).filter((i) => i.severity === 'error')).toEqual([]);
    for (const v of cs.values) expect(e.store.doc(v.parameter.split('#')[0])?.parameters?.[v.parameter.split('#')[1]], v.parameter).toBeDefined();
    expect(cs.accepts).toEqual([{ scope: 'p', release: '1.6.0' }, { scope: 'uae.p', release: '1.6.0' }, { scope: 'uae.wps', release: '1.6.0' }]);
    expect(Object.fromEntries(cs.values.map((v: any) => [v.parameter, v.value]))).toEqual({
      'p.table.rail-choice.v1#enabledRails': ['uae.wps'],
      'p.post.accounting.v1#vatPercent': 5,
      'p.post.accounting.v1#settlementAccount': 'CB-NOSTRO-WPS',
      'p.post.accounting.v1#suspenseAccount': 'WPS-SUSPENSE-GL',
      'p.post.accounting.v1#chargeIncomeAccount': 'WPS-CHARGE-GL',
      'p.post.accounting.v1#vatAccount': 'WPS-VAT-GL',
      'p.protect.encrypt-file.v1#recipientKey': 'cbuae-wps',
      // The source's twenty-minute acknowledgement wait, elapsed time, inside the bound from uae.wps 1.3.0 (Issue I-116).
      'uae.wps.exchange.submit-salary-file.v1#ackWait': 'PT20M',
      'uae.wps.exchange.submit-salary-file.v1#ackWaitCalendar': 'elapsed',
    });
    // No routing code: the source holds none for the bank (README.md), so the factory default, none, stays.
    expect(cs.values.map((v: any) => v.parameter)).not.toContain('uae.wps.rule.agent-is-this-bank.v1#ownRoutingCodes');
  });

  it('has a lock file equal to the one the studio generates, and exports without simulators', () => {
    const e = env();
    expect(compareManifest(e, 'b', TENANT)).toEqual({ equal: true, differences: [] });
    const pack = buildPack(e, 'b', 'bank', TENANT);
    expect(pack.refused).toBeUndefined();
    expect(pack.manifest.tenant).toBe(TENANT);
    expect(pack.manifest.artefacts.map((a: any) => a.kind)).not.toContain('simulator');
  });

  it("runs the bank's tests on the interpreter", async () => {
    const e = env();
    const tests = own(e).filter((x) => x.kind === 'test');
    expect(tests.map((t) => t.name).sort()).toEqual(['BANK-AML-001', 'BANK-AML-002', 'BANK-NTF-001', 'BANK-RAIL-001', 'BANK-RTE-001', 'BANK-WPS-001', 'BANK-WPS-002', 'BANK-WPS-003']);
    for (const t of tests) {
      const r = await runTest(e, t.doc);
      expect(r.passed, `${t.name}: ${r.error ?? JSON.stringify(r.checks.filter((c) => !c.passed))}`).toBe(true);
    }
  }, 120000);

  it('binds the labour ministry notification its WPS workflow calls, and not the AML check no workflow calls', async () => {
    const cs = env().store.configurationSet(TENANT);
    // This fails when a binding is added to or removed from the bank's configuration-set.yaml. Switching a hook on or off is
    // the bank's decision: change this test in the same change, with its reason.
    expect((cs.bindings ?? []).map((b: any) => `${b.hook} at ${b.hookPoint}`)).toEqual([`${NOTIFY} at p.wf.corporate.v1#on-close`]);
    const r = await runTest(env(), salaryFile('settled', 'reject'));
    expect(r.passed, `${r.error ?? ''} ${JSON.stringify(r.checks.filter((c) => !c.passed))}`).toBe(true);
    const traces = r.record!.instances.map((i: any) => i.trace);
    expect(traces).toEqual(expect.arrayContaining([['tell-labour-ministry:notified', 'end:done']]));
    expect(traces.flat()).not.toContain('employer-aml:hit');
    const steps = r.record!.instances.flatMap((i: any) => i.steps.map((s: any) => s.activity));
    expect(steps).toEqual(expect.arrayContaining(['p.protect.encrypt-file.v1', 'uae.wps.enrich.employer-status.v1', 'b.notify.labour-ministry.v1']));
    expect(steps).not.toContain('b.screen.employer-aml.v1');
    // A ministry that refuses the notification does not change the payment (the hook fails open, as the source's did).
    const ministry = await runTest(env(), salaryFile('settled', 'accept', 'reject'));
    expect(ministry.passed, JSON.stringify(ministry.checks.filter((c) => !c.passed))).toBe(true);
  }, 120000);

  it('refuses the file of an employer the labour ministry has suspended, and sends nothing (Issue I-88)', async () => {
    const r = await runTest(env(), salaryFile('rejected', 'accept', 'accept', 'suspended'));
    expect(r.passed, `${r.error ?? ''} ${JSON.stringify(r.checks.filter((c) => !c.passed))}`).toBe(true);
    expect(r.record!.messages.filter((m: any) => m.type === 'uae.wps.sif-submission')).toEqual([]);
  }, 60000);

  it('sends nothing the encryption service refuses (Issue I-87)', async () => {
    const r = await runTest(env(), salaryFile('rejected', 'accept', 'accept', 'accept', 'reject'));
    expect(r.passed, `${r.error ?? ''} ${JSON.stringify(r.checks.filter((c) => !c.passed))}`).toBe(true);
    expect(r.record!.messages.filter((m: any) => m.type === 'uae.wps.sif-submission')).toEqual([]);
  }, 60000);

  it('accepts the AML check bound as its README gives it, and then stops a file whose employer the service refuses', async () => {
    const cs = env().store.configurationSet(TENANT);
    expect(checkDocument(env(), { ...cs, bindings: AML_ON }, TENANT).filter((i) => i.severity === 'error')).toEqual([]);
    const refused = await runTest(env(), salaryFile('rejected', 'reject'), { bindings: AML_ON });
    expect(refused.passed, `${refused.error ?? ''} ${JSON.stringify(refused.checks.filter((c) => !c.passed))}`).toBe(true);
    expect(refused.record!.messages.filter((m: any) => m.type === 'uae.wps.sif-submission')).toEqual([]);
  }, 60000);
});
