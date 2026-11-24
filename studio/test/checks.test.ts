import { describe, expect, it } from 'vitest';
import { lintScript } from '../src/interp/sandbox.js';
import { Checker, interfaceDiff, semverIssue, checkAll, factoryPackIssues } from '../src/checks/checks.js';
import { freshEnv, clone } from './helpers.js';

// Mutation cases: each breaks the repository once and expects its check to fire (CLAUDE.md, Checks).

function docsOf() {
  const env = freshEnv();
  return { env, docs: env.store.current().map((e) => clone(e.doc)) };
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
  it('warns on the planned messages of a scheme (L10)', () => {
    // The WPS scheme's last planned messages (the agent's refund cycle) are built since release 1.2.0 (R-12-21).
    const env = freshEnv();
    expect(checkAll(env).filter((i) => i.code === 'L10')).toEqual([]);
    const planned = fire('L10', (find) => {
      find('uae.wps').messages.push({ id: 'uae.wps.later', title: 'A later message', direction: 'in', confirmed: false, disposition: { planned: 'Later' } });
    });
    expect(planned).toEqual([]); // a warning, not an error
    const { docs } = docsOf();
    docs.find((d) => d.kind === 'scheme' && d.scope === 'uae.wps').messages.push({ id: 'uae.wps.later', title: 'A later message', direction: 'in', confirmed: false, disposition: { planned: 'Later' } });
    expect(new Checker(docs, []).checkAll().filter((i) => i.code === 'L10').map((i) => i.key)).toEqual(['uae.wps']);
  });
  it('holds the factory to the packs its inventory lists (L34, R-12-11)', () => {
    const env = freshEnv();
    const spec = env.store.all().filter((e) => e.source === 'spec');
    const inventory = spec.find((e) => e.kind === 'inventory')!.doc;
    const files = spec.map((e) => ({ path: e.path, doc: e.doc }));
    expect(factoryPackIssues(inventory, ['p', 'uae.p', 'uae.wps'], files)).toEqual([]);
    // a folder the inventory does not list, as a removed scheme left behind would be
    expect(factoryPackIssues(inventory, ['p', 'uae.p', 'uae.wps', 'uae.retired'], files).map((i) => i.key)).toEqual(['uae.retired']);
    // a listed pack with no folder
    expect(factoryPackIssues(inventory, ['p', 'uae.p'], files).map((i) => i.message)).toEqual(['The inventory lists pack uae.wps, which has no folder under factory/']);
    // an artefact of another scope in a pack's folder
    const stray = [...files, { path: 'factory/uae.wps/ports/uae.retired.port.switch.v1.yaml', doc: { kind: 'port', name: 'uae.retired.port.switch.v1' } }];
    expect(factoryPackIssues(inventory, ['p', 'uae.p', 'uae.wps'], stray).map((i) => i.key)).toEqual(['uae.retired.port.switch.v1']);
    expect(factoryPackIssues(undefined, ['p'], files)[0].code).toBe('L34');
  });
});

describe('mutation cases', () => {
  const cases: [string, string, (f: (n: string) => any, docs: any[]) => void][] = [
    ['L02', 'a platform block depends on a scheme', (f) => { f('p.validate.canonical.v1').ruleSets = ['uae.wps.ruleset.salary-file.v1']; }],
    ['L03', 'a reference does not resolve', (f) => { f('p.wf.corporate.v1').do[1].call = 'p.validate.nothing.v1'; }],
    ['L04', 'the major in the name is not the release major', (f) => { f('p.reserve.funds.v1').release = '2.0.0'; }],
    ['L05', 'a handle entry is removed', (f) => { const st = f('uae.wps.wf.salary-file-out.v1').do.find((s: any) => s.call && s.handle); delete st.handle[Object.keys(st.handle)[0]]; }],
    ['L06', 'a hook step names an undeclared hook point', (f) => { f('p.wf.corporate.v1').do.splice(1, 0, { hook: 'nowhere' }); }],
    // The factory has no handler (I-38), so the case adds a minimal synthetic one.
    ['L07', 'a handler waits', (_f, docs) => { docs.push({ kind: 'workflow', wfKind: 'handler', name: 'uae.wps.handler.synthetic.v1', release: '1.0.0', dsl: '1.0.0', title: 'Synthetic handler', exit: { outcomes: ['answered'] }, do: [{ wait: 'PT1M' }, { end: 'answered' }], provenance: { origin: 'factory', status: 'provisional' } }); }],
    ['L08', 'a leg returns an outcome its slot does not allow', (f) => { f('uae.wps.wf.salary-file-out.v1').exit.outcomes.push('vanished'); }],
    ['L09', 'shared content names a bank', (f) => { f('p.reserve.funds.v1').title = 'Reserve funds at ExampleBankName'; }],
    ['L10', 'a deliver message is awaited by nothing', (f) => { f('uae.wps').messages.push({ id: 'uae.wps.orphan', title: 'x', direction: 'in', confirmed: false, disposition: { deliver: { correlateBy: 'file id' } } }); }],
    ['L12', 'a validation set holds a rule that yields a decision', (f) => { f('uae.wps.rule.currency-aed.v1').then = [{ yield: { decision: { name: 'x', value: 1 } } }]; }],
    ['L13', 'a transform hook writes outside its contract', (f) => { f('b.enrich.cost-centre.v1').writes.push('payment.purpose'); }],
    ['L14', 'a bank value is outside its bounds', (f) => { f('config').values[1].value = 2000000; }],
    ['L15', 'a bench names a scenario the simulator lacks', (f) => { f('WPS-OUT-001').bench[0].scenario = 'nonsense'; }],
    ['L16', 'an await names a message that is not delivered', (f) => { f('uae.wps.sub.submit-salary-file.v1').do.find((s: any) => s.await).await.message = 'uae.wps.sif-submission'; }],
    ['L17', 'a rule uses an undeclared parameter', (f) => { delete f('p.rule.amount-within-platform-limit.v1').parameters; }],
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
    ['L33', 'a start message routes to a workflow whose slot its leg does not fill', (f) => { const st = f('uae.wps').messages.find((m: any) => m.id === 'uae.wps.sif').disposition.start; st.direction = 'inward'; delete st.segment; }],
    ['L33', 'an outward start message names no segment', (f) => { delete f('uae.wps').messages.find((m: any) => m.id === 'uae.wps.sif').disposition.start.segment; }],
    ['L24', 'a step uses a construct outside the portable core', (f) => { f('p.wf.request.v1').do.splice(0, 0, { id: 'fan', race: { branches: [] } }); }],
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
    const wf = docs.find((d) => d.name === 'p.wf.commercial.v1');
    expect(new Checker(docs).check(wf).some((i) => i.code === 'L21')).toBe(true);
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

describe('every attempt is at least once (L54, R-03-25, R-03-26, Issue I-303)', () => {
  it('needs an idempotency key on an activity with an outside effect, read from the header, the same on every attempt', () => {
    expect(fire('L54', () => {})).toEqual([]);
    const noKey = fire('L54', (find) => {
      const a = find('p.notify.channel.v1');
      a.effect = 'read';
      delete a.idempotencyKey;
    });
    expect(noKey.map((i) => i.message)).toEqual([expect.stringMatching(/p\.notify\.channel\.v1 has an outside effect \(it is a notify\)/)]);
    const clock = fire('L54', (find) => { find('p.notify.channel.v1').idempotencyKey = 'concat(header.paymentId, now())'; });
    expect(clock).toHaveLength(1);
    const send = fire('L54', (find) => {
      const a = find('uae.wps.exchange.send-salary-file.v1');
      a.effect = 'read';
      delete a.idempotencyKey;
    });
    expect(send.map((i) => i.message)).toEqual([expect.stringMatching(/an exchange that sends/)]);
  });
});

describe("a scheme message's declarations beside its disposition (L55, R-09-209 to R-09-212)", () => {
  const msg = (find: (n: string) => any, id: string) => find('uae.wps').messages.find((m: any) => m.id === id);
  it('holds a file block, a reply, a report and a correlation map to what they name', () => {
    expect(fire('L55', (find) => { msg(find, 'uae.wps.pif').file = { namePattern: '[0-9]{12}\\.PIF', maxBytes: 1000, encrypted: true }; })).toEqual([]);
    expect(fire('L55', (find) => { msg(find, 'uae.wps.pif').file = { namePattern: '([0-9' }; })).toHaveLength(1);
    expect(fire('L55', (find) => { msg(find, 'uae.wps.ack').repliesTo = ['uae.wps.pif']; }).map((i) => i.message)).toEqual([expect.stringMatching(/which the bank does not send/)]);
    expect(fire('L55', (find) => { msg(find, 'uae.wps.ack').repliesTo = ['uae.wps.nothing']; })).toHaveLength(1);
    expect(fire('L55', (find) => { msg(find, 'uae.wps.ack').repliesTo = ['uae.wps.sif-submission']; })).toEqual([]);
    expect(fire('L55', (find) => { msg(find, 'uae.wps.psi').disposition.deliver.correlationMap = 'uae.wps.map.sif-out.v1'; }).map((i) => i.message)).toEqual([expect.stringMatching(/not an inward map/)]);
    const report = (rec: any) => (find: (n: string) => any) => { find('uae.wps').messages.push({ id: 'uae.wps.test-report', title: 'A report', direction: 'in', disposition: { reconcile: rec } }); };
    expect(fire('L55', report({ activity: 'p.reconcile.scheme-report.v1', map: 'uae.wps.map.psi-in.v1', period: 'P1D' }))).toEqual([]);
    expect(fire('L55', report({ activity: 'p.post.verify.v1', map: 'uae.wps.map.sif-out.v1', period: 'P1D' }))).toHaveLength(2);
  });
});
