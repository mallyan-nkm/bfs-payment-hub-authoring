import { describe, expect, it } from 'vitest';
import { runTest } from '../src/testing/runner.js';
import { checkDocument } from '../src/checks/checks.js';
import type { StudioEnv } from '../src/env.js';
import { freshEnv } from './helpers.js';

// The bank's two hooks in content-configurations/banks/dib/ (plan W6): the employer AML check and the labour
// ministry notification. They are built and tested, and the shipped configuration keeps them switched off: no
// live salary-file workflow of the source calls either step (README.md, "Not bound"). The runs here bind them in a
// configuration that exists only in the test, as a bank would when it switches them on.

const TENANT = 'dib';
const AML = 'b.hook.employer-aml-check.v1';
const NOTIFY = 'b.hook.labour-ministry-notify.v1';
let shared: StudioEnv | undefined;
const env = () => (shared ??= freshEnv({ banks: [TENANT] }));

/** The bindings README.md tells a bank to add to switch the hooks on, from the day of the run. */
const SWITCHED_ON = [
  { hookPoint: 'p.seg.check-and-screen.v1#after-validate', hook: AML, release: '1.0.0', validFrom: '2026-11-24' },
  { hookPoint: 'p.wf.corporate.v1#on-close', hook: NOTIFY, release: '1.0.0', validFrom: '2026-11-24' },
];

const bench = (aml: string, ministry: string) => [
  ...['core-banking', 'sanctions', 'fraud', 'channel', 'payment-record'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'release' },
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
  { port: 'b.port.aml-screening.v1', simulator: 'b.sim.aml-screening.v1', scenario: aml },
  { port: 'b.port.labour-ministry.v1', simulator: 'b.sim.labour-ministry.v1', scenario: ministry },
];

/** A synthetic salary file from the employer's channel, end to end through p.wf.corporate.v1, for the bank. */
const salaryFile = (aml: string, ministry: string, outcome: string) => ({
  kind: 'test', id: `DIB-HOOK-${aml}-${ministry}`, title: 'A salary file with the bank hooks bound', target: 'p.wf.corporate.v1', level: 'end-to-end',
  evaluationDate: '2026-11-24', tenant: TENANT, bench: bench(aml, ministry), given: { inbound: 'samples/wps-sif-valid.sif', message: 'uae.wps.sif' },
  expect: { outcome },
});

describe("the bank's hooks, switched off in the shipped configuration (plan W6)", () => {
  it('ships with neither hook bound, so a salary file runs no bank step', async () => {
    const cs = env().store.configurationSet(TENANT);
    const bound = (cs.bindings ?? []).filter((b: any) => [AML, NOTIFY].includes(b.hook)).map((b: any) => `${b.hook} at ${b.hookPoint}`);
    // This fails when a binding of either hook is added to content-configurations/banks/dib/configuration-set.yaml.
    // Switching them on is the bank's decision: change this test in the same change, with its reason.
    expect(bound, 'the shipped configuration binds a bank hook').toEqual([]);
    const r = await runTest(env(), salaryFile('reject', 'reject', 'settled'));
    expect(r.passed, JSON.stringify(r.checks.filter((c) => !c.passed))).toBe(true);
    expect(r.record?.instances.flatMap((i: any) => i.trace).filter((t: string) => t.startsWith('hook:'))).toEqual([]);
  }, 60000);

  it('accepts the bindings README.md gives for switching them on', () => {
    const cs = env().store.configurationSet(TENANT);
    expect(checkDocument(env(), { ...cs, bindings: SWITCHED_ON }, TENANT).filter((i) => i.severity === 'error')).toEqual([]);
  });

  it('bound in a test-only configuration: the AML service clears the employer, the file settles and the ministry is told', async () => {
    const r = await runTest(env(), salaryFile('accept', 'accept', 'settled'), { bindings: SWITCHED_ON });
    expect(r.passed, `${r.error ?? ''} ${JSON.stringify(r.checks.filter((c) => !c.passed))} ${JSON.stringify(r.trace)}`).toBe(true);
    const traces = r.record!.instances.map((i: any) => i.trace);
    expect(traces).toEqual(expect.arrayContaining([['employer-aml:clear', 'end:continue'], ['tell-labour-ministry:notified', 'end:done']]));
    expect(traces.flat()).toEqual(expect.arrayContaining(['hook:continue', 'hook:done', 'end:settled']));
    const steps = r.record!.instances.flatMap((i: any) => i.steps.map((s: any) => s.activity));
    expect(steps).toEqual(expect.arrayContaining(['b.screen.employer-aml.v1', 'b.notify.labour-ministry.v1']));
  }, 60000);

  it('bound: an employer the AML service refuses stops the file before it is sent', async () => {
    const r = await runTest(env(), salaryFile('reject', 'accept', 'rejected'), { bindings: SWITCHED_ON });
    expect(r.passed, `${r.error ?? ''} ${JSON.stringify(r.checks.filter((c) => !c.passed))} ${JSON.stringify(r.trace)}`).toBe(true);
    const sent = r.record!.messages.filter((m: any) => m.type === 'uae.wps.sif-submission');
    expect(sent).toEqual([]);
  }, 60000);

  it('bound: a labour ministry that refuses the notification does not change the payment (fails open)', async () => {
    const r = await runTest(env(), salaryFile('accept', 'reject', 'settled'), { bindings: SWITCHED_ON });
    expect(r.passed, `${r.error ?? ''} ${JSON.stringify(r.checks.filter((c) => !c.passed))} ${JSON.stringify(r.trace)}`).toBe(true);
  }, 60000);
});
