import { describe, expect, it } from 'vitest';
import { dispatchAs as dispatch } from './support/as-user.js';
import { freshEnv } from './helpers.js';

// A draft run (R-09-230, Issue I-341): a designer runs a new activity and its tests on the bench in their workspace before
// publishing, marked as made with drafts. Nothing is published by it, and only published releases go into a distribution.

const ACT = {
  kind: 'activity', name: 'p.validate.draft-period.v1', release: '1.0.0', title: 'The period of a file is in order', type: 'validate',
  reads: ['header'], writes: ['facts.period'], outcomes: [{ name: 'pass', primary: true }, { name: 'reject' }],
  effect: 'pure', implementation: { binding: 'script', language: 'typescript', script: 'scripts/p.validate.draft-period.v1.ts' },
};
const SRC = "export async function run({ ctx }) {\n  const ok = /^\\d{4}-\\d{2}$/.test(String(ctx.header.period ?? ''));\n  ctx.facts = { ...(ctx.facts ?? {}), period: { ok } };\n  return ok ? 'pass' : { outcome: 'reject', reasonCode: 'DT01' };\n}\n";
const TEST = (id: string, period: string, outcome: string) => ({
  kind: 'test', id, title: `A file of period ${period}`, target: ACT.name, level: 'activity', evaluationDate: '2026-11-24',
  given: { context: { header: { paymentId: 'P-1', period } } }, expect: { outcome },
});

describe('a draft run on the bench (R-09-230, Issue I-341)', () => {
  it('runs a draft activity and its draft tests, marked as a draft run, and publishes nothing; approval publishes the three', async () => {
    const env = freshEnv();
    const call = (method: string, path: string, body?: any, user?: string) => dispatch(env, { method, path, query: {}, body: body ?? {}, ...(user ? { user } : {}) });
    const saved = await call('POST', '/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: ACT });
    expect(saved.status, JSON.stringify(saved.body)).toBe(200);
    expect(saved.body.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
    expect((await call('POST', '/api/scripts', { tenant: 'owner', actor: 'shared.designer', activity: ACT.name, source: SRC })).status).toBe(200);

    // Without drafts the bench runs published releases only, and says the target is a draft.
    const plain = await call('POST', '/api/bench/run', { target: ACT.name, context: { header: { paymentId: 'P-1', period: '2026-11' } } });
    expect(plain.status).toBe(404);
    expect(plain.body.error).toMatch(/it is a draft/);

    // With drafts: the draft and its script run, and the report says so.
    const pass = await call('POST', '/api/bench/run', { target: ACT.name, drafts: true, context: { header: { paymentId: 'P-1', period: '2026-11' } } });
    expect(pass.status, JSON.stringify(pass.body).slice(0, 400)).toBe(200);
    expect(pass.body.outcome).toBe('pass');
    expect(pass.body.draftRun.drafts).toContain(`${ACT.name}@1.0.0`);
    expect(pass.body.draftRun.note).toMatch(/A draft run: .*not published/);
    const reject = await call('POST', '/api/bench/run', { target: ACT.name, drafts: true, context: { header: { paymentId: 'P-1', period: 'November' } } });
    expect(reject.body.outcome).toBe('reject');
    // A published run is not marked.
    const published = await call('POST', '/api/bench/run', { target: 'p.validate.limits.v1', context: { header: { paymentId: 'P-1' } } });
    expect(published.body.draftRun).toBeUndefined();

    // Its tests, saved from the bench as drafts, go into the activity's change set; they run as draft runs.
    const t1 = await call('POST', '/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: TEST('P-DRAFT-PERIOD-001', '2026-11', 'pass') });
    const t2 = await call('POST', '/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: TEST('P-DRAFT-PERIOD-002', 'November', 'reject') });
    expect([t1.status, t2.status]).toEqual([200, 200]);
    expect([t1.body.draft.changeSet, t2.body.draft.changeSet]).toEqual([saved.body.draft.changeSet, saved.body.draft.changeSet]);
    const listed = (await call('GET', '/api/tests', { tenant: 'owner' })).body.filter((t: any) => t.id.startsWith('P-DRAFT-PERIOD-'));
    expect(listed.map((t: any) => [t.id, t.draft])).toEqual([['P-DRAFT-PERIOD-001', true], ['P-DRAFT-PERIOD-002', true]]);
    for (const id of ['P-DRAFT-PERIOD-001', 'P-DRAFT-PERIOD-002']) {
      const r = await call('POST', `/api/tests/${id}/run`, { tenant: 'owner' });
      expect(r.status).toBe(200);
      expect(r.body.passed, JSON.stringify(r.body.checks)).toBe(true);
      expect(r.body.draftRun.drafts).toEqual(expect.arrayContaining([`${ACT.name}@1.0.0`, id]));
    }

    // Nothing was published: the store still holds drafts only, and a distribution carries no draft.
    expect(env.store.latest(ACT.name)).toBeUndefined();
    const dist = await call('POST', '/api/distributions', { bank: 'example-bank', engine: 'temporal' }, 'shared.checker');
    expect(dist.status, JSON.stringify(dist.body).slice(0, 400)).toBe(200);
    expect(JSON.stringify(dist.body.manifest)).not.toContain(ACT.name);

    // Approval publishes the activity, its script and its tests together; the bench then runs it without drafts.
    const keys = [saved.body.draft.key, t1.body.draft.key, t2.body.draft.key];
    const approved = await call('POST', '/api/publish', { tenant: 'owner', keys, checker: 'shared.checker' });
    expect(approved.status, JSON.stringify(approved.body).slice(0, 600)).toBe(200);
    expect(env.store.latest(ACT.name)?.status).toBe('published');
    expect(env.store.test('P-DRAFT-PERIOD-001')).toBeDefined();
    const after = await call('POST', '/api/bench/run', { target: ACT.name, context: { header: { paymentId: 'P-1', period: '2026-11' } } });
    expect([after.body.outcome, after.body.draftRun]).toEqual(['pass', undefined]);
  }, 240000);

  it('runs another tenant\'s drafts never: a bank\'s draft run sees its own drafts and shared content', async () => {
    const env = freshEnv();
    const call = (method: string, path: string, body?: any) => dispatch(env, { method, path, query: {}, body: body ?? {} });
    expect((await call('POST', '/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: ACT })).status).toBe(200);
    const r = await call('POST', '/api/bench/run', { tenant: 'example-bank', actor: 'example.maker', target: ACT.name, drafts: true, context: { header: { paymentId: 'P-1', period: '2026-11' } } });
    expect(r.status).toBe(404);
  });
});
