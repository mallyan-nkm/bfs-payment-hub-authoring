import { describe, expect, it } from 'vitest';
import { checkAll, checkDocument } from '../src/checks/checks.js';
import { buildPack, compareManifest } from '../src/governance/packs.js';
import * as gov from '../src/governance/governance.js';
import { runTest } from '../src/testing/runner.js';
import type { StudioEnv } from '../src/env.js';
import { freshEnv } from './helpers.js';

// Two real banks' folders on the content branch, content-configurations/banks/<bank>/ (owner's decision of
// 2026-11-22; D5 as amended; Issue I-43). Neither runs WPS in the source, so each configures the platform only: no rail
// of the factory's, no routing code, no WPS dial. Each carries the employer AML check and the labour ministry
// notification from its own WPS fork as hooks, built and tested and switched off: no live workflow of the bank in the
// source calls them (each README.md). The runs that bind them do so in a configuration that exists only in the test.

// ADIB runs WPS by the owner's decision of 2026-11-22 (Issue I-86): its folder is tested in adib-wps.test.ts.
const BANKS = ['enbd', 'fab'];
const AML = 'b.hook.employer-aml-check.v1';
const NOTIFY = 'b.hook.labour-ministry-notify.v1';

const bench = (aml: string, ministry: string) => [
  ...['core-banking', 'sanctions', 'fraud', 'channel', 'payment-record'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'release' },
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
  { port: 'b.port.aml-screening.v1', simulator: 'b.sim.aml-screening.v1', scenario: aml },
  { port: 'b.port.labour-ministry.v1', simulator: 'b.sim.labour-ministry.v1', scenario: ministry },
];

/** A synthetic salary file from the employer's channel, end to end through p.wf.corporate.v1, for the bank. */
const salaryFile = (tenant: string, aml: string, ministry: string, outcome: string) => ({
  kind: 'test', id: `PLATFORM-BANK-${aml}-${ministry}`, title: 'A salary file for a bank that runs no factory rail', target: 'p.wf.corporate.v1', level: 'end-to-end',
  evaluationDate: '2026-11-24', tenant, bench: bench(aml, ministry), given: { inbound: 'samples/wps-sif-valid.sif', message: 'uae.wps.sif' },
  expect: { outcome },
});

/** What a bank would add to switch the hooks on, and WPS with them: test-only, from the day of the run. */
const SWITCHED_ON = [
  { hookPoint: 'p.seg.check-and-screen.v1#after-validate', hook: AML, release: '1.0.0', validFrom: '2026-11-24' },
  { hookPoint: 'p.wf.corporate.v1#on-close', hook: NOTIFY, release: '1.0.0', validFrom: '2026-11-24' },
];
const WPS_ON = [{ parameter: 'p.table.rail-choice.v1#enabledRails', value: ['uae.wps'], validFrom: '2026-11-24' }];

describe.each(BANKS)('the bank pack in content-configurations/banks/%s (platform only)', (tenant) => {
  let shared: StudioEnv | undefined;
  const env = () => (shared ??= freshEnv({ banks: [tenant] }));
  const own = (e: StudioEnv) => e.store.all().filter((x) => x.tenant === tenant);

  it('loads as its tenant, only when asked, and leaves the runtime formats out', () => {
    const e = env();
    expect(e.store.bankPacks.get(tenant)).toBe(tenant);
    const kinds = own(e).map((x) => x.kind);
    expect(kinds).toEqual(expect.arrayContaining(['pack', 'configuration-set', 'hook', 'activity', 'port', 'simulator', 'test']));
    expect(kinds).not.toContain('port-bindings');
    expect(own(e).every((x) => x.scope === 'b' || x.kind === 'test')).toBe(true);
    expect(freshEnv().store.tenants()).not.toContain(tenant);
  });

  it('passes the schemas and the static checks, and sets only platform parameters the factory declares', () => {
    const e = env();
    for (const x of own(e)) expect(e.schemas.validate(x.doc), x.key).toEqual([]);
    const names = new Set(own(e).map((x) => x.name));
    expect(checkAll(e).filter((i) => i.severity === 'error')).toEqual([]);
    expect(checkAll(e).filter((i) => names.has(i.key))).toEqual([]);
    const cs = e.store.configurationSet(tenant);
    expect(gov.checkConfiguration(e, tenant, cs).filter((i) => i.severity === 'error')).toEqual([]);
    for (const v of cs.values) expect(e.store.doc(v.parameter.split('#')[0])?.parameters?.[v.parameter.split('#')[1]], v.parameter).toBeDefined();
    // The bank does not run WPS in the source: no rail, and no WPS dial.
    expect(cs.values.map((v: any) => [v.parameter, v.value])).toEqual([['p.table.rail-choice.v1#enabledRails', []]]);
  });

  it('has a lock file equal to the one the studio generates, and exports without simulators', () => {
    const e = env();
    expect(compareManifest(e, 'b', tenant)).toEqual({ equal: true, differences: [] });
    const pack = buildPack(e, 'b', 'bank', tenant);
    expect(pack.refused).toBeUndefined();
    expect(pack.manifest.tenant).toBe(tenant);
    expect(pack.manifest.artefacts.map((a: any) => a.kind)).not.toContain('simulator');
  });

  it("runs the bank's tests on the interpreter", async () => {
    const e = env();
    const tests = own(e).filter((x) => x.kind === 'test');
    expect(tests.map((t) => t.name).sort()).toEqual(['BANK-AML-001', 'BANK-AML-002', 'BANK-NTF-001', 'BANK-RAIL-001', 'BANK-RTE-001']);
    for (const t of tests) {
      const r = await runTest(e, t.doc);
      expect(r.passed, `${t.name}: ${r.error ?? JSON.stringify(r.checks.filter((c) => !c.passed))}`).toBe(true);
    }
  }, 120000);

  it('ships with neither hook bound and no rail: a salary file is refused at routing, sent nowhere, and runs no bank step', async () => {
    const cs = env().store.configurationSet(tenant);
    // This fails when a binding is added to the bank's configuration-set.yaml. Switching a hook on is the bank's
    // decision: change this test in the same change, with its reason.
    expect(cs.bindings ?? []).toEqual([]);
    const r = await runTest(env(), salaryFile(tenant, 'reject', 'reject', 'rejected'));
    expect(r.passed, `${r.error ?? ''} ${JSON.stringify(r.checks.filter((c) => !c.passed))}`).toBe(true);
    expect(r.record!.instances.flatMap((i: any) => i.trace)).toContain('route:no-match');
    expect(r.record!.instances.flatMap((i: any) => i.trace).filter((t: string) => t.startsWith('hook:'))).toEqual([]);
    expect(r.record!.messages.filter((m: any) => m.type === 'uae.wps.sif-submission')).toEqual([]);
  }, 60000);

  it('accepts the bindings its README gives for switching the hooks on', () => {
    const cs = env().store.configurationSet(tenant);
    expect(checkDocument(env(), { ...cs, bindings: SWITCHED_ON }, tenant).filter((i) => i.severity === 'error')).toEqual([]);
  });

  it('bound with WPS on in a test-only configuration: cleared and told, refused and stopped, a ministry refusal changes nothing', async () => {
    const cleared = await runTest(env(), salaryFile(tenant, 'accept', 'accept', 'settled'), { bindings: SWITCHED_ON, values: WPS_ON });
    expect(cleared.passed, `${cleared.error ?? ''} ${JSON.stringify(cleared.checks.filter((c) => !c.passed))}`).toBe(true);
    expect(cleared.record!.instances.map((i: any) => i.trace)).toEqual(expect.arrayContaining([['employer-aml:clear', 'end:continue'], ['tell-labour-ministry:notified', 'end:done']]));

    const refused = await runTest(env(), salaryFile(tenant, 'reject', 'accept', 'rejected'), { bindings: SWITCHED_ON, values: WPS_ON });
    expect(refused.passed, `${refused.error ?? ''} ${JSON.stringify(refused.checks.filter((c) => !c.passed))}`).toBe(true);
    expect(refused.record!.messages.filter((m: any) => m.type === 'uae.wps.sif-submission')).toEqual([]);

    const ministry = await runTest(env(), salaryFile(tenant, 'accept', 'reject', 'settled'), { bindings: SWITCHED_ON, values: WPS_ON });
    expect(ministry.passed, `${ministry.error ?? ''} ${JSON.stringify(ministry.checks.filter((c) => !c.passed))}`).toBe(true);
  }, 120000);
});
