import { describe, expect, it } from 'vitest';
import { lintScript } from '../src/interp/sandbox.js';
import { Checker, interfaceDiff, semverIssue, checkAll, factoryPackIssues } from '../src/checks/checks.js';
import { freshEnv, clone } from './helpers.js';
import { runActivity } from '../src/interp/interpreter.js';

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
    // Releases relative to the one in the factory (1.1.0 since factory 1.23.0).
    const [ma, mi] = String(prev.release).split('.').map(Number);
    const next = clone(prev);
    next.release = `${ma}.${mi + 1}.0`;
    next.outcomes.push({ name: 'partial' });
    expect(interfaceDiff(prev, next).required).toBe('major');
    expect(semverIssue(prev, next)?.code).toBe('L26');
    const minor = clone(prev);
    minor.release = `${ma}.${mi}.1`;
    minor.policy.attemptTimeout = 'PT20S';
    expect(interfaceDiff(prev, minor).required).toBe('minor');
    expect(semverIssue(prev, minor)?.message).toMatch(new RegExp(`Propose ${ma}\\.${mi + 1}\\.0`));
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
      const a = find('uae.wps.exchange.send-salary-file.v2');
      a.effect = 'read';
      delete a.idempotencyKey;
    });
    expect(send.map((i) => i.message)).toEqual([expect.stringMatching(/an exchange that sends/)]);
  });
});

describe('a builtin a native module answers says so (L40, R-09-282, R-09-296, Issues I-381, I-382, I-387)', () => {
  it('a builtin of the accounting or reservation service says native: true, and native: true goes with a script or a builtin', () => {
    expect(fire('L40', () => {})).toEqual([]);
    expect(fire('L40', (find) => { delete find('p.post.accounting.v1').implementation.native; }).map((i) => i.message))
      .toEqual([expect.stringMatching(/p\.post\.accounting\.v1 is a builtin of the accounting service, which a native module answers on the runtime and the image does not; it says native: true/)]);
    expect(fire('L40', (find) => { delete find('p.reserve.release.v1').implementation.native; }).map((i) => i.message))
      .toEqual([expect.stringMatching(/p\.reserve\.release\.v1 is a builtin of the reservation service/)]);
    // Since factory 1.24.0 a scheme's own builtin of the accounting service is asked too (R-09-296, Issue I-387).
    expect(fire('L40', (find) => { delete find('uae.wps.post.return.v1').implementation.native; }).map((i) => i.message))
      .toEqual([expect.stringMatching(/uae\.wps\.post\.return\.v1 is a builtin of the accounting service, which a native module answers/)]);
    // A builtin the image answers (the payment record) needs no module.
    expect(fire('L40', (find) => { delete find('p.validate.duplicate.v1').implementation.native; })).toEqual([]);
    expect(fire('L40', (find) => { find('p.decide.await-posting-window.v1').implementation.native = true; }).map((i) => i.message))
      .toEqual([expect.stringMatching(/its binding is subflow; native: true needs a script or a builtin/)]);
  });
});

describe('a send after an encryption sends the encrypted file, and each sent message names its one send (L58, L59; R-05-101, R-05-102)', () => {
  it('L58: a send after the subflow encrypts the file reads facts.encryption (Issue I-293)', () => {
    expect(fire('L58', () => {})).toEqual([]);
    const plain = fire('L58', (find) => { const a = find('uae.wps.exchange.send-refund-response.v2'); a.reads = a.reads.filter((r: string) => r !== 'facts.encryption'); });
    expect(plain.map((i) => i.message)).toEqual([expect.stringMatching(/sends uae\.wps\.exchange\.send-refund-response\.v2 after step .*encrypt.* reads no facts\.encryption/)]);
  });
  it('L59: every message the bank sends names a send of its scheme, and no send sends two (Issue I-310)', () => {
    expect(fire('L59', () => {})).toEqual([]);
    const two = fire('L59', (find) => { find('uae.wps').messages.find((m: any) => m.id === 'uae.wps.dcr').disposition.sentBy = 'uae.wps.exchange.send-refund-request.v2'; });
    expect(two.map((i) => i.message)).toEqual([expect.stringMatching(/are both sent by uae\.wps\.exchange\.send-refund-request\.v2/)]);
    const map = fire('L59', (find) => { find('uae.wps').messages.find((m: any) => m.id === 'uae.wps.rfa').disposition.sentBy = 'uae.wps.map.rfa-out.v1'; });
    expect(map.map((i) => i.message)).toEqual([expect.stringMatching(/uae\.wps\.rfa is sent by uae\.wps\.map\.rfa-out\.v1, which is no send/)]);
  });
});

describe('a message names the kinds an earlier runtime kept it under (L63, R-09-302, Issue I-402)', () => {
  const sif = (find: (n: string) => any) => find('uae.wps').messages.find((m: any) => m.id === 'uae.wps.sif-submission');
  it('the salary file names its former kind, and a former kind is its scheme\'s name, no message\'s id, and one message\'s', () => {
    expect(fire('L63', () => {})).toEqual([]);
    expect(fire('L63', (find) => { sif(find).formerKinds = ['uae.wps.ack']; }).map((i) => i.message))
      .toEqual([expect.stringMatching(/uae\.wps\.sif-submission's former kind uae\.wps\.ack is the id of a message of uae\.wps/)]);
    expect(fire('L63', (find) => { sif(find).formerKinds = ['p.sif-out']; }).map((i) => i.message))
      .toEqual([expect.stringMatching(/former kind p\.sif-out is not a name of uae\.wps/)]);
    expect(fire('L63', (find) => { find('uae.wps').messages.find((m: any) => m.id === 'uae.wps.ack').formerKinds = ['uae.wps.sif-out']; }).map((i) => i.message))
      .toEqual([expect.stringMatching(/uae\.wps\.sif-submission and uae\.wps\.ack both name the former kind uae\.wps\.sif-out/)]);
  });
});

describe('a scripted check without rule sets may be pure (L57, R-03-27, Issue I-342)', () => {
  const CHECK = {
    kind: 'activity', name: 'b.validate.pay-period-check.v1', release: '1.0.0', title: 'The pay period is in order', type: 'validate', reads: ['header'], writes: ['facts.payPeriod'],
    outcomes: [{ name: 'pass', primary: true }, { name: 'reject' }], effect: 'pure', implementation: { binding: 'script', language: 'typescript', script: 'scripts/b.validate.pay-period-check.v1.ts' },
  };
  it('the schema takes pure or read for a validate without rule sets, and nothing else', () => {
    const env = freshEnv();
    expect(env.schemas.validate(CHECK)).toEqual([]);
    expect(env.schemas.validate({ ...CHECK, effect: 'read' })).toEqual([]);
    expect(env.schemas.validate({ ...CHECK, effect: 'write', idempotencyKey: 'header.paymentId' }).length).toBeGreaterThan(0);
  });
  it('a pure activity names no port, and a pure check without rule sets is a script', () => {
    expect(fire('L57', () => {})).toEqual([]);
    expect(fire('L57', (find) => { find('p.decide.cut-off.v1').effect = 'pure'; })).toEqual([]);
    expect(fire('L57', (find) => { find('p.validate.duplicate.v1').effect = 'pure'; }).map((i) => i.message)).toEqual([expect.stringMatching(/is pure but names the port p\.port\.payment-record\.v1/), expect.stringMatching(/bound to a builtin, not a script/)]);
    expect(fire('L57', (find) => { find('p.decide.await-posting-window.v1').effect = 'pure'; }).map((i) => i.message)).toEqual([expect.stringMatching(/bound to a subflow, not a script/)]);
    // Pure, like read, needs no idempotency key (L54).
    expect(fire('L54', (find) => { find('p.decide.cut-off.v1').effect = 'pure'; })).toEqual([]);
  });
  it('the interpreter runs a pure scripted check, and refuses a port call from one', async () => {
    const env = freshEnv();
    const store = env.store.shadow();
    store.add(CHECK, 'banks/example-bank/activities/b.validate.pay-period-check.v1.yaml', 'studio', false, 'published', 'example-bank');
    const script = (source: string) => env.scripts.withFirst([{ name: CHECK.name, language: 'typescript', file: 'content:pay-period-check.ts', source }]);
    const ok = await runActivity({ store, scripts: script("export async function run({ ctx }) {\n  ctx.facts = { payPeriod: { days: 30 } };\n  return ctx.header.paymentId ? 'pass' : 'reject';\n}\n"), tenant: 'example-bank', evaluationDate: '2026-11-24', bench: [], signals: [] } as any, CHECK.name, { header: { paymentId: 'P-1' } });
    expect(ok.root?.outcome).toBe('pass');
    const calls = await runActivity({ store, scripts: script("export async function run({ ports }) {\n  await ports.request('p.port.payment-record.v1');\n  return 'pass';\n}\n"), tenant: 'example-bank', evaluationDate: '2026-11-24', bench: [], signals: [] } as any, CHECK.name, { header: { paymentId: 'P-1' } });
    expect(JSON.stringify(calls.run.timeline)).toMatch(/b\.validate\.pay-period-check\.v1 is pure and reached port p\.port\.payment-record\.v1/);
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
