import { describe, expect, it } from 'vitest';
import { dispatch } from '../src/server/api.js';
import { freshEnv } from './helpers.js';

// The workflow debugger (F-17; docs/ISSUES.md I-32): a debug run keeps the data after every event,
// what each step decided, which branch a switch took, and the documents each instance ran.

const BENCH = ['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' }))
  .concat([{ port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' }, { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' }]);
const PAYMENT = { target: 'p.wf.corporate.v1', tenant: 'example-bank', inbound: 'samples/wps-sif-two-lines.sif', message: 'uae.wps.sif', bench: BENCH };
const post = (env: any, path: string, body: any) => dispatch(env, { method: 'POST', path, query: {}, body });

describe('the debugger', () => {
  it('runs a payment end to end and keeps the data after every event, only where it changed', async () => {
    const env = freshEnv();
    const r = await post(env, '/api/debug/run', PAYMENT);
    expect(r.status).toBe(200);
    expect(r.body.trace.at(-1)).toBe('end:settled');
    const tl: any[] = r.body.record.timeline;
    // Every event of an instance either carries the data, or says it is the same as the instance's
    // last. The front door's own events belong to no instance.
    expect(tl.filter((e) => e.instance !== 'front-door').every((e) => e.data !== undefined || e.same || e.omitted)).toBe(true);
    expect(tl.filter((e) => e.data).length).toBeGreaterThan(10);
    // The data really follows the run: the rail decision is in the data after the route step.
    const route = tl.find((e) => e.type === 'outcome' && e.step === 'route');
    const after = [...tl].slice(0, tl.indexOf(route) + 1).reverse().find((e) => e.instance === route.instance && e.data)?.data;
    expect(after.facts.route.rail).toBe('uae.wps');
  });

  it('says what each step decided and found', async () => {
    const env = freshEnv();
    const tl: any[] = (await post(env, '/api/debug/run', PAYMENT)).body.record.timeline;
    const route = tl.find((e) => e.type === 'outcome' && e.step === 'route').detail;
    expect(route).toMatchObject({ outcome: 'decided', activity: 'p.decide.rail.v1', decisions: { rail: 'uae.wps' }, worst: 'pass', rulesFired: ['p.table.rail-choice.v1'] });
    expect(route.delta[0].path).toBe('facts.route');
    expect(tl.some((e) => e.type === 'slot' && e.detail.leg === 'uae.wps.wf.salary-file-out.v1')).toBe(true);
  });

  it('gives each instance its document, and each segment it uses', async () => {
    const env = freshEnv();
    const rec = (await post(env, '/api/debug/run', PAYMENT)).body.record;
    const byKind = Object.fromEntries(rec.instances.map((i: any) => [i.kind, i.id]));
    expect(rec.docs[byKind.platform].name).toBe('p.wf.corporate.v1');
    expect(rec.docs[byKind.leg].name).toBe('uae.wps.wf.salary-file-out.v1');
    expect(rec.docs['use:p.seg.check-and-screen.v1'].name).toBe('p.seg.check-and-screen.v1');
  });

  it('records which branch a switch took', async () => {
    const env = freshEnv();
    const r = await post(env, '/api/tests/AANI-OUT-004/run', { config: { debug: true } });
    const branch = r.body.record.timeline.find((e: any) => e.type === 'branch');
    expect(branch, JSON.stringify(r.body.trace)).toBeDefined();
    expect(branch.detail).toHaveProperty('case');
  });

  it('changes nothing in a run that is not a debug run', async () => {
    const env = freshEnv();
    const plain = await post(env, '/api/bench/run', PAYMENT);
    const debug = await post(freshEnv(), '/api/debug/run', PAYMENT);
    expect(plain.body.trace).toEqual(debug.body.trace);
    const tl: any[] = plain.body.record.timeline;
    expect(tl.some((e) => 'data' in e || 'same' in e)).toBe(false);
    expect(tl.some((e) => e.type === 'branch')).toBe(false);
    expect(plain.body.record.docs).toBeUndefined();
  });
});
