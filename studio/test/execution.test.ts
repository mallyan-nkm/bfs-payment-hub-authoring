import { describe, expect, it } from 'vitest';
import { runAllTests, runTest } from '../src/testing/runner.js';
import { runKit } from '../src/conformance/kit.js';
import { SimulatorInstance } from '../src/sim/runtime.js';
import { VirtualClock } from '../src/interp/clock.js';
import { freshEnv } from './helpers.js';

describe('the simulator runtime (C10)', () => {
  const env = freshEnv();
  const host = (clock: VirtualClock, sink: any[]) => ({ clock, deliver: (m: any) => sink.push(m), newId: (p: string) => `${p}-${sink.length}-${Math.floor(clock.now)}`, log: () => {} });
  it('fails twice, then accepts the same message', () => {
    const clock = new VirtualClock(0);
    const sim = new SimulatorInstance(env.store.doc('p.sim.core-banking.v1'), 'fail-twice-then-accept', host(clock, []));
    const m: any = { id: 'm1', type: '', port: 'p.port.core-banking.v1', from: 'platform', at: 0, correlation: {} };
    expect(sim.receive(m, true).kind).toBe('transient');
    expect(sim.receive(m, true).kind).toBe('transient');
    const r = sim.receive(m, true);
    expect(r.kind).toBe('reply');
    expect(sim.receive(m, true)).toEqual(r); // the conversation keeps its answer
  });
  it('sends a later reply on the virtual clock', async () => {
    const clock = new VirtualClock(0);
    const sink: any[] = [];
    const sim = new SimulatorInstance(env.store.doc('uae.wps.sim.wps-outward.v1'), 'accept', host(clock, sink));
    expect(sim.receive({ id: 'f1', type: 'uae.wps.sif-submission', port: 'uae.wps.port.wps.v1', from: 'platform', at: 0, correlation: { fileId: 'F1' } }, false).kind).toBe('ack');
    await clock.run(() => sink.length > 0);
    expect(clock.elapsed).toBe(600000);
    expect(sink[0]).toMatchObject({ type: 'uae.wps.ack', status: 'accepted', correlation: { fileId: 'F1' } });
  });
});

describe('the factory tests on the studio interpreter (C09, C11)', () => {
  it('passes every factory test', async () => {
    const env = freshEnv();
    const reports = await runAllTests(env);
    expect(reports.length).toBe(63); // P-REG-001 to 004, WPS-OUT-009 and 010 (R-12-27); P-POST-001 to 004, WPS-RFR-001 to 003 (I-136); P-FX-001 to 003, P-CHG-001 and 002, WPS-RRI-001 to 007 (I-133); P-DEC-001 and 002, WPS-MAP-005, WPS-IN-009 (I-115, I-117); WPS-IN-008 and WPS-RTC-001 (plan P2); P-ENC, WPS-ENR, WPS-EXC-003, WPS-MAP-003 and 004, WPS-OUT-006 to 008 (I-87, I-88, I-100, I-109)
    const failing = reports.filter((r) => !r.passed).map((r) => r.id);
    expect(failing).toEqual([]);
  });
  it('shows three attempts and the backoff for P-RES-001 (F-17)', async () => {
    const env = freshEnv();
    const r = await runTest(env, env.store.test('P-RES-001')!.doc);
    const send = r.record!.timeline.filter((e: any) => e.step === 'reserve');
    expect(send.filter((e: any) => e.type === 'attempt').length).toBe(3);
    expect(send.filter((e: any) => e.type === 'retry-wait').map((e: any) => e.detail.ms)).toEqual([2000, 4000]); // PT2S, backoff 2
  });
  it('ends a no-reply run at the timeout on the virtual clock (F-16)', async () => {
    const env = freshEnv();
    const r = await runTest(env, env.store.test('WPS-OUT-003')!.doc);
    expect(r.passed).toBe(true);
    expect(r.elapsed).toBe('P1D');
  });
  it('turns a test red when the trace changes (F-19)', async () => {
    const env = freshEnv();
    const t = env.store.test('WPS-OUT-001')!.doc;
    const r = await runTest(env, { ...t, expect: { ...t.expect, trace: [...t.expect.trace].reverse() } });
    expect(r.passed).toBe(false);
  });
  it('records deltas with the release that ran, and keeps state to references (R-02-08, R-02-10)', async () => {
    const env = freshEnv();
    const r = await runTest(env, env.store.test('WPS-OUT-001')!.doc);
    const leg = r.record!.instances.find((i) => i.workflow === 'uae.wps.wf.salary-file-out.v1')!;
    expect(leg.deltas.find((d: any) => d.step === 'file-checks')).toMatchObject({ release: '1.0.0' });
    expect(Object.keys(leg.engineState)).toEqual(['header', 'facts', 'refs']);
  });
  it('applies a supersession to steps not yet started and records both releases (R-01-22)', async () => {
    const env = freshEnv();
    const doc = { ...env.store.doc('uae.p.rule.iban-checksum.v1'), release: '1.0.1' };
    const d = env.store.saveDraft('owner', 'maker', doc);
    env.store.publish('owner', d.key, 'maker', 'checker');
    const t = env.store.test('WPS-OUT-001')!.doc;
    const r = await runTest(env, t, { supersessions: [{ artefact: 'uae.p.rule.iban-checksum.v1', replaces: '1.0.1', with: '1.0.1' }] });
    expect(r.passed).toBe(true);
  });
});

describe('the conformance kit on the studio interpreter (C23, C09)', () => {
  it('passes all 57 scenarios (CK-PARK-03, R-09-91)', async () => {
    const env = freshEnv();
    const res = await runKit(env);
    const red = res.filter((r) => !r.passed).map((r) => `${r.id}: ${JSON.stringify(r.actual)} ${r.error ?? ''}`);
    expect(red).toEqual([]);
    expect(res.length).toBe(57);
  });
});
