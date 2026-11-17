import { describe, expect, it } from 'vitest';
import { lintScript } from '../src/interp/sandbox.js';
import { Checker, interfaceDiff, semverIssue, checkAll } from '../src/checks/checks.js';
import { freshEnv, clone } from './helpers.js';

// Mutation cases: each breaks the repository once and expects its check to fire (CLAUDE.md, Checks).

function docsOf() {
  const env = freshEnv();
  return { env, docs: env.store.all().filter((e) => e.status === 'published').map((e) => clone(e.doc)) };
}

function fire(code: string, mutate: (find: (name: string) => any, docs: any[]) => void | any) {
  const { docs } = docsOf();
  const find = (n: string) => docs.find((d) => d.name === n || d.id === n || (d.kind === 'scheme' && d.scope === n) || (d.kind === 'configuration-set' && n === 'config'));
  mutate(find, docs);
  const issues = new Checker(docs, ['examplebankname']).checkAll();
  return issues.filter((i) => i.code === code && i.severity === 'error');
}

describe('static checks over the store (C04)', () => {
  it('finds no error in the spec as delivered, as the reference validator does', () => {
    const env = freshEnv();
    const errors = checkAll(env).filter((i) => i.severity === 'error');
    expect(errors).toEqual([]);
  });
  it('warns on the planned messages of both schemes (L10)', () => {
    const env = freshEnv();
    const w = checkAll(env).filter((i) => i.code === 'L10');
    expect(w.map((i) => i.key).sort()).toEqual(['uae.aani', 'uae.wps']);
  });
});

describe('mutation cases', () => {
  const cases: [string, string, (f: (n: string) => any, docs: any[]) => void][] = [
    ['L02', 'a platform block depends on a scheme', (f) => { f('p.validate.canonical.v1').ruleSets = ['uae.aani.ruleset.credit.v1']; }],
    ['L03', 'a reference does not resolve', (f) => { f('p.wf.corporate.v1').do[1].call = 'p.validate.nothing.v1'; }],
    ['L04', 'the major in the name is not the release major', (f) => { f('p.reserve.funds.v1').release = '2.0.0'; }],
    ['L05', 'a handle entry is removed', (f) => { delete f('uae.aani.wf.credit-out.v1').do[0].handle.reject; }],
    ['L06', 'a hook step names an undeclared hook point', (f) => { f('p.wf.corporate.v1').do.splice(1, 0, { hook: 'nowhere' }); }],
    ['L07', 'a handler waits', (f) => { f('uae.aani.handler.status-inquiry-in.v1').do.splice(0, 0, { wait: 'PT1M' }); }],
    ['L08', 'a leg returns an outcome its slot does not allow', (f) => { f('uae.aani.wf.credit-out.v1').exit.outcomes.push('vanished'); }],
    ['L09', 'shared content names a bank', (f) => { f('p.reserve.funds.v1').title = 'Reserve funds at ExampleBankName'; }],
    ['L10', 'a deliver message is awaited by nothing', (f) => { f('uae.wps').messages.push({ id: 'uae.wps.orphan', title: 'x', direction: 'in', confirmed: false, disposition: { deliver: { correlateBy: 'file id' } } }); }],
    ['L12', 'a validation set holds a rule that yields a decision', (f) => { f('uae.aani.rule.amount-within-limit.v1').then = [{ yield: { decision: { name: 'x', value: 1 } } }]; }],
    ['L13', 'a transform hook writes outside its contract', (f) => { f('b.enrich.cost-centre.v1').writes.push('payment.purpose'); }],
    ['L14', 'a bank value is outside its bounds', (f) => { f('config').values[1].value = 90000; }],
    ['L15', 'a bench names a scenario the simulator lacks', (f) => { f('AANI-OUT-001').bench[0].scenario = 'nonsense'; }],
    ['L16', 'an await names a message that is not delivered', (f) => { f('uae.wps.sub.submit-salary-file.v1').do.find((s: any) => s.await).await.message = 'uae.wps.sif-submission'; }],
    ['L17', 'a rule uses an undeclared parameter', (f) => { delete f('uae.aani.rule.amount-within-limit.v1').parameters; }],
    ['L18', 'an activity writes an unregistered extension path', (f) => { f('uae.wps.map.sif-in.v1').writes.push('ext.uae.nowhere'); }],
    ['L20', 'a write is retried without an idempotency key', (f) => { delete f('p.reserve.funds.v1').idempotencyKey; }],
    ['L22', 'a port has no simulator', (_f, docs) => { const i = docs.findIndex((d) => d.name === 'uae.p.sim.calendar.v1'); docs.splice(i, 1); }],
    ['L25', 'a first table has no catch-all', (f) => { f('p.table.rail-choice.v1').rows.pop(); }],
    ['L27', 'a supersession moves across major lines', (f) => { f('SUP-2026-001').with = '2.0.0'; }],
    ['L30', 'a composite declares an outcome its subflow never returns', (f) => { f('uae.wps.exchange.submit-salary-file.v1').outcomes.push({ name: 'vanished' }); }],
    ['L30', 'a subflow ends the workflow instead of returning', (f) => { f('uae.wps.sub.submit-salary-file.v1').do.push({ end: 'settled' }); }],
    ['L30', 'a step that calls a composite has no id', (f) => { delete f('uae.wps.wf.salary-file-out.v1').do.find((s: any) => s.call === 'uae.wps.exchange.submit-salary-file.v1').id; }],
    ['L31', 'a leg awaits a message itself', (f) => { f('uae.wps.wf.salary-file-out.v1').do.splice(1, 0, { id: 'wait-ack', await: { message: 'uae.wps.ack', correlateBy: 'header.fileId', timeout: 'P1D', onTimeout: { end: 'pending' } } }); }],
    ['L32', 'an await has no timeout and its composite no reply time', (f) => { delete f('uae.wps.exchange.submit-salary-file.v1').policy; }],
    ['L29', 'two steps share an id after inlining', (f) => { f('p.wf.corporate.v1').do[1].id = 'validate'; }],
    ['L23', 'an exported workflow misses a pin', (f) => { f('p.wf.request.v1').artefacts = [{ name: 'p.notify.channel.v1', release: '1.0.0', hash: 'sha256:' + '0'.repeat(64) }]; f('p.wf.request.v1').do.splice(0, 0, { id: 'x', call: 'p.validate.canonical.v1', handle: { repair: { end: 'rejected' }, reject: { end: 'rejected' } } }); }],
  ];
  for (const [code, what, mutate] of cases) {
    it(`${code} fires when ${what}`, () => {
      expect(fire(code, mutate).length, code).toBeGreaterThan(0);
    });
  }
  it('L19 warns when a step reads what nothing wrote', () => {
    const { docs } = docsOf();
    const wf = docs.find((d) => d.name === 'p.wf.corporate.v1');
    wf.entry.reads = ['header'];
    const w = new Checker(docs).check(wf).filter((i) => i.code === 'L19');
    expect(w.some((i) => i.step === 'validate' && /payment/.test(i.message))).toBe(true);
  });
  it('L21 warns when the budgets do not fit the deadline', () => {
    const { docs } = docsOf();
    const leg = docs.find((d) => d.name === 'uae.aani.wf.credit-out.v1');
    expect(new Checker(docs).check(leg).some((i) => i.code === 'L21')).toBe(true);
  });
  it('L26 proposes a major when the outcome list changes (F-22)', () => {
    const { env } = docsOf();
    const prev = env.store.doc('p.reserve.funds.v1');
    const next = clone(prev);
    next.release = '1.1.0';
    next.outcomes.push({ name: 'partial' });
    expect(interfaceDiff(prev, next).required).toBe('major');
    expect(semverIssue(prev, next)?.code).toBe('L26');
    const minor = clone(prev);
    minor.release = '1.0.1';
    minor.policy.attemptTimeout = 'PT20S';
    expect(interfaceDiff(prev, minor).required).toBe('minor');
    expect(semverIssue(prev, minor)?.message).toMatch(/Propose 1.1.0/);
  });
  it('L28 flags a script that reads the system clock', () => {
    void 0;
    expect(lintScript('export async function run() { return String(Date.now()); }').length).toBe(1);
  });
});
