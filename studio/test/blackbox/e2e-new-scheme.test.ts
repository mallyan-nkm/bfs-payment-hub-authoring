import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { parseYaml, toYaml } from '../../src/core/yaml.js';
import { unzip } from '../../src/export/zip.js';
import { client, startServer, STUDIO, type Server } from './server.js';

// End to end, black box: a designer builds a new scheme from scratch through the HTTP API: its
// manifest and dispatch table, port, simulator, maps with their scripts, rule, exchange and leg.
// The leg fills the scheme-leg slot of the corporate payment workflow. A bank enables the new rail
// in its configuration; then a file from the bank's channel runs as a payment: front door, intake,
// workflow-select, the corporate workflow, and the new leg in its slot. The export has a separate
// worker per task queue, the new scheme's own among them, and runs deployed on DTFx.
//
// `uae.demo` is a synthetic scheme made up for this test. It stands for no real scheme.

import { S, MAKER, CHECKER, BANK, PROV, HEADER, n, PORT, reply, SIM, RULE, RULESET, VALIDATE, FILE_IN, FILE_IN_SCRIPT, FILE_OUT, ACK_IN, SEND, SUBMIT, SUBFLOW, LEG, SCHEME, VALID, USD, GARBAGE, bench, T, TESTS } from './demo-scheme.js';

let server: Server;
let api: ReturnType<typeof client>;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-scheme-'));
beforeAll(async () => {
  server = await startServer();
  api = client(server.url);
}, 60000);
afterAll(() => server?.stop());

async function draft(doc: any) {
  const saved = await api.post('/api/drafts', { tenant: 'owner', actor: MAKER, doc });
  expect(saved.status, JSON.stringify(saved.data)).toBe(200);
  const errors = saved.data.issues.filter((i: any) => i.severity === 'error');
  expect(errors, `${doc.name ?? doc.scope ?? doc.id}: ${JSON.stringify(errors)}`).toEqual([]);
  return saved.data.draft.key;
}
async function publish(doc: any, tests: any[] = []) {
  const key = await draft(doc);
  if (tests.length) {
    const gates = await api.post('/api/gates', { doc, tests });
    expect(gates.data.passed, `${doc.name}: ${JSON.stringify(gates.data.firstRed)}`).toBe(true);
  }
  expect((await api.post('/api/review', { tenant: 'owner', actor: MAKER, key })).status).toBe(200);
  const pub = await api.post('/api/publish', { tenant: 'owner', key, checker: CHECKER });
  expect(pub.status, `${doc.name ?? doc.scope}: ${JSON.stringify(pub.data)}`).toBe(200);
}
const changeSets: string[] = [];
/** Drafts that refer to one another, reviewed and published together as one change set. */
async function publishSet(docs: any[]) {
  const keys: string[] = [];
  for (const doc of docs) {
    const saved = await api.post('/api/drafts', { tenant: 'owner', actor: MAKER, doc });
    expect(saved.status, JSON.stringify(saved.data)).toBe(200);
    keys.push(saved.data.draft.key);
  }
  // With every member saved, each is clean: the checks read the tenant's drafts as a working set.
  const listed = (await api.get('/api/drafts?tenant=owner')).data.filter((d: any) => keys.includes(d.key));
  expect(listed.map((d: any) => [d.key, d.issues]), JSON.stringify(listed)).toEqual(keys.map((k) => [k, 0]));
  // The studio generates the change set: from any member, the same set (R-11-19).
  for (const key of keys) {
    const cs = await api.get(`/api/change-set?tenant=owner&key=${encodeURIComponent(key)}`);
    expect(cs.data.members.map((m: any) => m.key).sort()).toEqual([...keys].sort());
  }
  for (const key of keys) expect((await api.post('/api/review', { tenant: 'owner', actor: MAKER, key })).status).toBe(200);
  expect((await api.post('/api/publish', { tenant: 'owner', key: keys[0], checker: MAKER })).status).toBe(409); // the maker cannot approve it
  // Approving one member publishes the whole change set.
  const pub = await api.post('/api/publish', { tenant: 'owner', key: keys[0], checker: CHECKER });
  expect(pub.status, JSON.stringify(pub.data)).toBe(200);
  expect(pub.data.published.length).toBe(docs.length);
  changeSets.push(pub.data.changeSet.id);
}
async function runAll(tests: any[]) {
  for (const t of tests) {
    await publish(t);
    const r = await api.post(`/api/tests/${t.id}/run`);
    expect(r.data.passed, `${t.id} ${r.data.error ?? ''} ${JSON.stringify(r.data.checks?.filter((c: any) => !c.passed))} ${r.data.trace?.join(' | ')}`).toBe(true);
  }
}
function deploy(zip: string, scenario: string, sample: string) {
  const args = ['src/cli.ts', 'deploy', zip, '--workflow', 'p.wf.corporate.v1', '--message', `${S}.credit-file`, '--inbound', sample, '--tenant', BANK, ...bench(scenario, true).flatMap((b) => ['--bench', `${b.port}=${b.simulator}:${b.scenario}`])];
  const r = spawnSync(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), args, { cwd: STUDIO, encoding: 'utf8', env: { ...process.env }, timeout: 300000 });
  const out = (r.stdout ?? '') + (r.stderr ?? '');
  const workers = [...out.matchAll(/worker (th-[\w-]+) \(pid (\d+)\) served (\{.*\}), refused (\d+)/g)].map((m) => ({ queue: m[1], pid: m[2], served: JSON.parse(m[3]), refused: Number(m[4]) }));
  return { out, workers, code: r.status, outcome: /^outcome (\w+)/m.exec(out)?.[1], trace: /^trace  (.*)$/m.exec(out)?.[1].split(' | ') ?? [] };
}

describe('a new scheme built from scratch, connected to the payment workflow', () => {
  it('saves the synthetic sample files the tests use', async () => {
    for (const [name, content] of [['demo-credit-file-valid.csv', VALID], ['demo-credit-file-usd.csv', USD], ['demo-credit-file-garbage.csv', GARBAGE]]) {
      const r = await api.post('/api/samples', { tenant: 'owner', actor: MAKER, name, content });
      expect(r.status, JSON.stringify(r.data)).toBe(200);
    }
  });

  it('publishes the port with its simulator as one change set, then the rule and its rule set', async () => {
    await publishSet([PORT, SIM]);
    await publish(RULE);
    await publish(RULESET);
  });

  it('publishes the file checks and the intake map with its script, each with a test per outcome', async () => {
    await publish(VALIDATE, TESTS.validate);
    await runAll(TESTS.validate);
    await draft(FILE_IN);
    const script = await api.post('/api/scripts', { tenant: 'owner', actor: MAKER, activity: FILE_IN.name, source: FILE_IN_SCRIPT });
    expect(script.status, JSON.stringify(script.data)).toBe(200);
    const key = (await api.get('/api/drafts?tenant=owner')).data.find((d: any) => d.name === FILE_IN.name).key;
    await api.post('/api/review', { tenant: 'owner', actor: MAKER, key });
    expect((await api.post('/api/publish', { tenant: 'owner', key, checker: CHECKER })).status).toBe(200);
    await runAll(TESTS.fileIn);
  });

  it('publishes the render and acknowledgement maps and the send', async () => {
    await publish(FILE_OUT);
    await publish(ACK_IN);
    await publish(SEND);
  });

  it('publishes as one change set the scheme manifest, the composite with its subflow, and the leg that fills the corporate workflow\'s scheme-leg slot', async () => {
    // They refer to one another: the dispatch table names the leg and the intake map, the leg calls
    // the composite, the composite's subflow awaits a message of the scheme's catalogue.
    // The dispatch table routes (R-05-42): an entry with no direction, or one that routes to a
    // workflow whose slot the leg does not fill, is refused by the checks.
    const start = SCHEME.messages[0].disposition.start as any;
    const undirected = await api.post('/api/check', { doc: { ...SCHEME, messages: [{ ...SCHEME.messages[0], disposition: { start: { kind: 'payment', leg: LEG.name, intakeMap: FILE_IN.name } } }, ...SCHEME.messages.slice(1)] } });
    expect(undirected.data.issues.some((i: any) => i.code === 'L00' && /direction/.test(i.message))).toBe(true);
    await publishSet([SCHEME, SUBMIT, SUBFLOW, LEG]);
    // Published, the leg fills a slot of p.wf.corporate.v1: a release of the scheme that routed the
    // file as an inward payment would send it to p.wf.inward.v1, and L33 says so.
    const misrouted = await api.post('/api/check', { doc: { ...SCHEME, release: '1.0.1', messages: [{ ...SCHEME.messages[0], disposition: { start: { ...start, direction: 'inward', segment: undefined } } }, ...SCHEME.messages.slice(1)] } });
    expect(misrouted.data.issues.find((i: any) => i.code === 'L33')?.message).toContain(`routes to p.wf.inward.v1, but its leg ${LEG.name} fills a slot of p.wf.corporate.v1`);
    const x = await api.get(`/api/expanded/${LEG.name}`);
    expect(JSON.stringify(x.data.doc)).toContain('submit.await-ack');
    const scheme = (await api.get(`/api/schemes/${S}`)).data.scheme;
    expect(scheme.messages.find((m: any) => m.id === `${S}.credit-file`).disposition.start).toMatchObject({ kind: 'payment', leg: LEG.name, intakeMap: FILE_IN.name });
    expect((await api.get('/api/schemes')).data.map((s: any) => s.scope)).toContain(S);
  });

  it('passes every test of the composite and the leg, started from the dispatch table', async () => {
    await runAll(TESTS.submit);
    await runAll(TESTS.leg);
    for (const name of [SUBMIT.name, LEG.name]) expect((await api.get(`/api/test-coverage/${name}`)).data.untested).toEqual([]);
  });

  it('cannot pay yet: the bank has not enabled the new rail, so the route finds none', async () => {
    await publish(TESTS.e2e[0]);
    const r = await api.post(`/api/tests/${TESTS.e2e[0].id}/run`);
    expect(r.data.passed).toBe(false);
    expect(r.data.trace).toContain('route:no-match');
    expect(r.data.outcome).toBe('rejected');
  });

  it('pays end to end once the bank accepts the scheme and enables its rail in a new configuration', async () => {
    const cs = (await api.get(`/api/tenants/${BANK}/configuration`)).data.configurationSet;
    const next = JSON.parse(JSON.stringify(cs));
    next.release = '1.1.0';
    next.accepts.push({ scope: S, release: '1.0.0' });
    next.values.find((v: any) => v.parameter === 'p.table.rail-choice.v1#enabledRails').value.push(S);
    const check = await api.post(`/api/tenants/${BANK}/configuration/validate`, { doc: next });
    expect(check.data.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
    expect((await api.post(`/api/tenants/${BANK}/configuration`, { actor: 'example.maker', yaml: toYaml(next) })).status).toBe(200);
    const r = await api.post(`/api/tests/${TESTS.e2e[0].id}/run`);
    expect(r.data.passed, JSON.stringify(r.data.checks?.filter((c: any) => !c.passed))).toBe(true);
    expect(r.data.trace).toEqual(expect.arrayContaining(['route:decided', 'leg:settled', 'post:posted', 'end:settled']));
  });

  let zip = '';
  it('exports the payment workflow for the bank, with the new scheme on its own worker', async () => {
    const r = await api.get(`/api/export/download?workflows=p.wf.corporate.v1&target=dtfx&withSimulators=true&tenant=${BANK}`);
    expect(r.status).toBe(200);
    zip = path.join(work, 'corporate-demo.zip');
    fs.writeFileSync(zip, r.data);
    const files = new Map(unzip(fs.readFileSync(zip)).map((e) => [e.name, e.content.toString('utf8')]));
    const manifest = parseYaml(files.get('manifest.yaml')!);
    expect(manifest.workers.map((w: any) => w.queue)).toEqual(expect.arrayContaining(['th-bank', 'th-p', 'th-uae-p', 'th-uae-demo']));
    const demo = manifest.workers.find((w: any) => w.queue === 'th-uae-demo');
    expect(demo.tasks).toEqual(expect.arrayContaining([`${FILE_IN.name}@1.0.0`, `${VALIDATE.name}@1.0.0`, `${SEND.name}@1.0.0`, `${ACK_IN.name}@1.0.0`, `${FILE_OUT.name}@1.0.0`]));
    expect(demo.tasks.every((t: string) => t.startsWith(`${S}.`))).toBe(true);
    expect(parseYaml(files.get('binding.yaml')!).queues[S]).toBe('th-uae-demo'); // derived from the scope (R-09-05)
    expect(parseYaml(files.get(`schemes/${S}.yaml`)!).legs).toEqual([LEG.name]);
    expect(files.get(`workers/th-uae-demo/scripts/${FILE_IN.name}.js`)).toContain('mapped');
    expect(parseYaml(files.get(`configuration/${BANK}.yaml`)!).release).toBe('1.1.0');
    // The export lists the change sets its artefacts were published in (R-11-20).
    const listed = manifest.changeSets.map((c: any) => c.id);
    for (const id of changeSets) expect(listed).toContain(id);
    const demoSet = manifest.changeSets.find((c: any) => c.members.includes(`${LEG.name}@1.0.0`));
    expect(demoSet.kind).toBe('approval');
    expect(demoSet.members).toEqual(expect.arrayContaining([`scheme:${S}@1.0.0`, `${SUBMIT.name}@1.0.0`]));
    expect(demoSet.checker).toBe(CHECKER);
  });

  it('deployed on DTFx: the credit file is paid end to end, each worker a separate process serving only its queue', () => {
    const d = deploy(zip, 'accept', server.contentFile('samples/demo-credit-file-valid.csv'));
    expect(d.out).toContain('intake uae.demo.map.file-in.v1 on th-uae-demo: mapped');
    expect(d.out).toContain('front door: workflow-select chose p.wf.corporate.v1 for payment outward corporate');
    expect(d.out).toContain(`front door: ${LEG.name} fills scheme-leg`);
    expect(d.outcome, d.out.slice(-1500)).toBe('settled');
    expect(d.trace).toEqual(expect.arrayContaining(['route:decided', 'leg:settled', 'post:posted', 'verify:verified', 'end:settled']));
    expect(new Set(d.workers.map((w) => w.pid)).size).toBe(d.workers.length);
    for (const w of d.workers) {
      expect(w.refused).toBe(0);
      const scope = w.queue === 'th-bank' ? 'b.' : w.queue === 'th-p' ? 'p.' : w.queue.replace(/^th-/, '').replace('-', '.') + '.';
      expect(Object.keys(w.served).every((t) => t.startsWith(scope)), `${w.queue}: ${Object.keys(w.served)}`).toBe(true);
    }
    const demo = d.workers.find((w) => w.queue === 'th-uae-demo')!;
    expect(demo.served[`${SEND.name}@1.0.0`]).toBe(1);
    expect(demo.served[`${ACK_IN.name}@1.0.0`]).toBe(1);
    expect(d.workers.find((w) => w.queue === 'th-uae-p')!.served['uae.p.validate.iban.v1@1.0.0']).toBe(2); // one per credit
    expect(Object.keys(d.workers.find((w) => w.queue === 'th-uae-wps')?.served ?? {})).toEqual([]); // another scheme, untouched
  }, 300000);

  it('deployed on DTFx: refused by the clearing house, the payment is rejected and its reservation undone', () => {
    const d = deploy(zip, 'reject', server.contentFile('samples/demo-credit-file-valid.csv'));
    expect(d.outcome, d.out.slice(-1500)).toBe('rejected');
    expect(d.trace).toEqual(expect.arrayContaining(['leg:rejected', 'undo:reserve', 'end:rejected']));
  }, 300000);

  it('deployed on DTFx: a file in dollars stops at the file checks and sends nothing', () => {
    const d = deploy(zip, 'accept', server.contentFile('samples/demo-credit-file-usd.csv'));
    expect(d.outcome, d.out.slice(-1500)).toBe('rejected');
    expect(d.workers.find((w) => w.queue === 'th-uae-demo')!.served[`${SEND.name}@1.0.0`]).toBeUndefined();
  }, 300000);
  // The same payment workflow, exported for Elsa Workflows 3 and deployed the same way (I-30): the
  // package is the same but for its binding; the engine harness is Elsa's.
  let elsaZip = '';
  it('exports the same workflow for Elsa: the same workers, an Elsa binding', async () => {
    const r = await api.get(`/api/export/download?workflows=p.wf.corporate.v1&target=elsa&withSimulators=true&tenant=${BANK}`);
    expect(r.status).toBe(200);
    elsaZip = path.join(work, 'corporate-demo-elsa.zip');
    fs.writeFileSync(elsaZip, r.data);
    const files = new Map(unzip(fs.readFileSync(elsaZip)).map((e) => [e.name, e.content.toString('utf8')]));
    const binding = parseYaml(files.get('binding.yaml')!);
    expect(binding.target).toBe('elsa');
    expect(binding.strategy).toBe('interpreter');
    expect(binding.queues[S]).toBe('th-uae-demo');
    expect(parseYaml(files.get('manifest.yaml')!).workers.map((w: any) => w.queue)).toEqual(expect.arrayContaining(['th-bank', 'th-p', 'th-uae-p', 'th-uae-demo']));
  });

  it('deployed on Elsa: the credit file is paid end to end, each worker a separate process serving only its queue', () => {
    const d = deploy(elsaZip, 'accept', server.contentFile('samples/demo-credit-file-valid.csv'));
    expect(d.out).toContain('front door: workflow-select chose p.wf.corporate.v1 for payment outward corporate');
    expect(d.outcome, d.out.slice(-1500)).toBe('settled');
    expect(d.trace).toEqual(expect.arrayContaining(['route:decided', 'leg:settled', 'post:posted', 'verify:verified', 'end:settled']));
    expect(new Set(d.workers.map((w) => w.pid)).size).toBe(d.workers.length);
    for (const w of d.workers) expect(w.refused).toBe(0);
    const demo = d.workers.find((w) => w.queue === 'th-uae-demo')!;
    expect(demo.served[`${SEND.name}@1.0.0`]).toBe(1);
    expect(d.workers.find((w) => w.queue === 'th-uae-p')!.served['uae.p.validate.iban.v1@1.0.0']).toBe(2);
  }, 300000);

  it('deployed on Elsa: refused by the clearing house, the payment is rejected and its reservation undone', () => {
    const d = deploy(elsaZip, 'reject', server.contentFile('samples/demo-credit-file-valid.csv'));
    expect(d.outcome, d.out.slice(-1500)).toBe('rejected');
    expect(d.trace).toEqual(expect.arrayContaining(['leg:rejected', 'undo:reserve', 'end:rejected']));
  }, 300000);
});
