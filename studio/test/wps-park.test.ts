import { describe, expect, it } from 'vitest';
import { durationMs } from '../src/core/duration.js';
import { runTest } from '../src/testing/runner.js';
import { freshEnv } from './helpers.js';

// R-12-26 (Issue I-125): a WPS payment flow's step that may run again parks for a business day when it fails, for an operator's
// retry, instead of ending the file. The salary file's render fails once: it parks, the operator retries it, and the file goes on
// to WPS as before. With no operator, the step fails at the end of its park, as it would have failed at once before.

describe('the WPS payment flows park a failed step that may run again (R-12-26)', () => {
  it('a salary file whose render fails parks, and settles once the operator retries it', async () => {
    const env = freshEnv();
    const t = env.store.test('WPS-OUT-001')!.doc;
    const r = await runTest(env, t, { stubs: { 'step:render': { failTimes: 1 } }, signals: [{ after: 'PT2H', signal: 'retry' }] } as any);
    expect(r.outcome, JSON.stringify(r.trace)).toBe('settled');
    expect(r.trace).toEqual(expect.arrayContaining(['render:stuck', 'operator:retry']));
    expect(r.trace.indexOf('render:stuck')).toBeLessThan(r.trace.indexOf('operator:retry'));
  });

  it('a step nobody retries fails at the end of its park, a business day later', async () => {
    const env = freshEnv();
    const t = env.store.test('WPS-OUT-001')!.doc;
    const r = await runTest(env, { ...t, expect: {} }, { stubs: { 'step:render': { error: 'BusinessReject' } } } as any);
    expect(r.trace).toContain('render:stuck');
    expect(r.trace).not.toContain('submit:accepted');
    expect(r.outcome).not.toBe('settled');
    expect(r.record!.elapsedMs).toBeGreaterThanOrEqual(durationMs('P1D'));
  });

  it('the agent leg and the return leg park their steps that may run again, and only those', () => {
    const env = freshEnv();
    const parks = (name: string) => {
      const out: string[] = [];
      const walk = (steps: any[]) => {
        for (const s of steps ?? []) {
          if (s.policy?.park) out.push(s.id);
          for (const v of Object.values<any>(s.handle ?? {})) if (Array.isArray(v)) walk(v);
          if (s.forEach) walk(s.forEach.do);
          if (s.try) walk(s.try);
        }
      };
      walk(env.store.doc(name).do);
      return out;
    };
    expect(parks('uae.wps.wf.salary-file-out.v1')).toEqual(['business-date', 'employer-status', 'file-checks', 'render']);
    expect(parks('uae.wps.wf.agent-payment-in.v2')).toEqual(['file-checks', 'render-receipt', 'render-disbursement', 'returns', 'render-returns', 'record-return']);
    expect(parks('uae.wps.wf.return-in.v1')).toEqual(['credit-employer']);
  });
});
