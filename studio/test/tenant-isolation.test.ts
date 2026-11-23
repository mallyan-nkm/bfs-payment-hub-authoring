import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { exportPackage } from '../src/export/exporter.js';
import { scopedEnv } from '../src/core/scope.js';
import { runTest } from '../src/testing/runner.js';
import type { StudioEnv } from '../src/env.js';
import { dispatchAs } from './support/as-user.js';
import { freshEnv } from './helpers.js';

// R-11-48, Issue I-120: the studio is multi-tenant. A bank's user reads, tests, exports and deploys shared content and its own
// bank's only. Here example-bank holds published artefacts, tests, simulators, an extension, a sample, a script and a draft in
// a change set; second-bank's user asks for each of them by every route that reads, and is given none, not even its name.

let env: StudioEnv;
const as = (user: string, method: string, p: string, body?: any) => {
  const [pathOnly, qs] = p.split('?');
  const query = Object.fromEntries(new URLSearchParams(qs ?? ''));
  return dispatchAs(env, { method, path: pathOnly, query, body: body ?? {}, user });
};
/** Every name of example-bank's own that another bank must never see. */
let secrets: string[] = [];
const replayFile = path.join(os.tmpdir(), `th-isolation-replay-${process.pid}.json`);

beforeAll(async () => {
  env = freshEnv();
  // Example-bank's own sample, script and draft, besides its published pack.
  const sample = await as('example.maker', 'POST', '/api/samples', { name: 'example-only-sample.csv', content: 'synthetic,1\n' });
  expect(sample.status, JSON.stringify(sample.body)).toBe(200);
  const draft = (await as('example.maker', 'GET', '/api/artefacts/b.notify.treasury.v1')).body.doc;
  const saved = await as('example.maker', 'POST', '/api/drafts', { doc: { ...draft, name: 'b.notify.example-only.v1', release: '1.0.0', title: 'Example draft only', implementation: { binding: 'script', language: 'typescript' } } });
  expect(saved.status, JSON.stringify(saved.body)).toBe(200);
  const script = await as('example.maker', 'POST', '/api/scripts', { activity: 'b.notify.example-only.v1', language: 'typescript', source: `export async function run() { return 'sent'; }` });
  expect(script.status, JSON.stringify(script.body)).toBe(200);
  // The bank's own: its pack and what the studio saved for it. A factory test that names the example bank as its tenant is shared
  // content (the shared packs list it), not the bank's.
  const own = env.store.all().filter((e) => e.tenant === 'example-bank' && !e.path.startsWith('factory/'));
  const sharedTitles = new Set(env.store.all().filter((e) => e.tenant === 'owner').map((e) => e.doc?.title));
  const titles = own.map((e) => e.doc?.title).filter((t) => typeof t === 'string' && !sharedTitles.has(t));
  secrets = [...new Set([...own.map((e) => e.name), ...titles, 'example-only-sample.csv', 'Example draft only', saved.body.draft.key])].filter((n) => n && n.length > 5);
  expect(secrets).toContain('b.validate.internal-limit.v1');
  expect(secrets).toContain('BANK-LIM-001');
  expect(secrets).toContain('b.sim.treasury.v1');
  expect(secrets).toContain('b.ext.bank.v1');
});
afterAll(() => fs.rmSync(replayFile, { force: true }));

/**
 * What second-bank's user is given must name none of example-bank's own. A name the caller sent itself may come back in a
 * refusal ("No artefact <name>"), so the names in the route and the body are taken out before the answer is searched.
 */
function expectNothingOfExample(route: string, body: any, asked: any = '') {
  let text = JSON.stringify(body ?? null);
  const sent = `${route} ${JSON.stringify(asked)}`;
  for (const n of secrets) if (sent.includes(n)) text = text.split(n).join('');
  const leaked = secrets.filter((n) => text.includes(n));
  expect(leaked, `${route}: ${text.slice(0, 400)}`).toEqual([]);
}

describe('a bank user reads only shared content and its own bank\'s (R-11-48)', () => {
  const GETS = [
    '/api/meta', '/api/catalogue', '/api/catalogue?q=b.', '/api/catalogue?kind=test', '/api/data', '/api/data?q=ext', '/api/checks', '/api/drafts',
    '/api/change-sets', '/api/tests', '/api/simulators', '/api/samples', '/api/model', '/api/model?q=ext', '/api/registry', '/api/supersessions',
    '/api/supersessions/handed-over', '/api/packs', '/api/packs/b/manifest', '/api/test-coverage', '/api/test-coverage/b.validate.internal-limit.v1',
    '/api/coverage/b', '/api/bindings', '/api/releases/p.notify.generic.v1/pins', '/api/propagate?name=p.notify.generic.v1&release=1.0.0',
    '/api/git', '/api/audit', '/api/users', '/api/tenants/second-bank/configuration', '/api/tenants/second-bank/port-bindings', '/api/tenants/second-bank/settable',
    '/api/conformance/report', '/api/kit',
  ];
  for (const route of GETS) {
    it(`GET ${route}`, async () => {
      const r = await as('second.maker', 'GET', route);
      expect(r.status, `${route}: ${String(JSON.stringify(r.body)).slice(0, 300)}`).toBeLessThan(500);
      expectNothingOfExample(route, r.body);
    });
  }

  it('reads none of its artefacts, tests, types, expansions or stubs by name, key or release, and is told nothing of them', async () => {
    for (const route of [
      '/api/artefacts/b.validate.internal-limit.v1', '/api/artefacts/b.validate.internal-limit.v1?release=1.0.0', '/api/artefacts/x?key=example-bank%2Fb.validate.internal-limit.v1%401.0.0',
      '/api/artefacts/BANK-LIM-001', '/api/tests/BANK-LIM-001', '/api/types/b.validate.internal-limit.v1', '/api/types/b.validate.internal-limit.v1?lang=python',
      '/api/expanded/b.hook.add-cost-centre.v1', '/api/simulators/b.sim.treasury.v1/stub', '/api/change-set?key=' + encodeURIComponent(env.store.drafts('example-bank')[0].key),
      '/api/tenants/example-bank/configuration', '/api/tenants/example-bank/port-bindings', '/api/tenants/example-bank/export',
    ]) {
      const r = await as('second.maker', 'GET', route);
      expect([403, 404], `${route}: ${r.status}`).toContain(r.status);
      expectNothingOfExample(route, r.body);
    }
    // The bank's own user reads them.
    expect((await as('example.maker', 'GET', '/api/artefacts/b.validate.internal-limit.v1')).status).toBe(200);
    expect((await as('example.maker', 'GET', '/api/tests/BANK-LIM-001')).status).toBe(200);
  });

  it('runs, tests, debugs, exports and packs none of them', async () => {
    const posts: [string, any][] = [
      ['/api/tests/BANK-LIM-001/run', {}],
      ['/api/bench/run', { target: 'b.validate.internal-limit.v1', context: {} }],
      ['/api/debug/run', { target: 'b.hook.add-cost-centre.v1', context: {} }],
      ['/api/export', { workflows: ['b.hook.add-cost-centre.v1'], target: 'temporal' }],
      ['/api/expanded', { doc: { kind: 'workflow', name: 'b.workflow.probe.v1', release: '1.0.0', wfKind: 'leg', steps: [{ call: 'b.validate.internal-limit.v1', id: 'x' }] } }],
      ['/api/packs/build', { scope: 'b', kind: 'bank' }],
      ['/api/tests/run-all', {}],
      ['/api/supersessions/test', { artefact: 'b.validate.internal-limit.v1', with: '1.0.0', workflows: ['b.hook.add-cost-centre.v1'] }],
      ['/api/tenants/second-bank/compatibility', {}],
    ];
    for (const [route, body] of posts) {
      const r = await as('second.maker', 'POST', route, body);
      expect(r.status, `${route}: ${JSON.stringify(r.body).slice(0, 300)}`).toBeLessThan(500);
      expectNothingOfExample(route, r.body, body);
    }
    const dl = await as('second.maker', 'GET', '/api/export/download?workflows=b.hook.add-cost-centre.v1&target=temporal');
    expect(dl.status).toBe(400);
  });

  it('a check of its own draft of the same name diffs against nothing of the other bank\'s', async () => {
    const doc = (await as('example.maker', 'GET', '/api/artefacts/b.validate.internal-limit.v1')).body.doc;
    const r = await as('second.maker', 'POST', '/api/check', { doc: { ...doc, release: '2.0.0', title: 'Second bank own limit' } });
    expect(r.status).toBe(200);
    expect(r.body.interface).toBeUndefined();
    expectNothingOfExample('/api/check', r.body.issues, doc);
    // Its own draft of that name is its own: saving it is not refused as published elsewhere.
    const saved = await as('second.maker', 'POST', '/api/drafts', { doc: { ...doc, release: '1.0.0', title: 'Second bank own limit' } });
    expect(saved.status, JSON.stringify(saved.body)).toBe(200);
    expect(saved.body.draft.key).toBe('second-bank/b.validate.internal-limit.v1@1.0.0#draft');
  });

  it('a sample and a script of one bank are never another bank\'s', () => {
    const second = scopedEnv(env, 'second-bank');
    expect([...second.store.sampleFiles.keys()].some((k) => k.includes('example-only-sample'))).toBe(false);
    expect(second.scripts.find('b.notify.example-only.v1')).toBeUndefined();
    expect(scopedEnv(env, 'example-bank').scripts.find('b.notify.example-only.v1')?.source).toMatch(/sent/);
    expect(env.store.sampleFiles.has('example-bank/example-only-sample.csv')).toBe(true);
  });

  it('a test that names another bank\'s sample cannot read it', async () => {
    const t = { kind: 'test', id: 'PROBE-1', title: 'probe', target: 'uae.wps.map.sif-in.v1', level: 'activity', evaluationDate: '2026-11-17', tenant: 'second-bank', given: { inbound: 'samples/example-only-sample.csv' }, expect: {} };
    const r = await runTest(env, t);
    expect(r.error ?? r.checks.map((c) => c.note).join(' ')).toMatch(/does not exist/);
  });

  it('an export for one bank holds nothing of another bank\'s', () => {
    const pkg = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'flowable', tenant: 'second-bank' });
    expectNothingOfExample('export', Object.keys(pkg.files));
    expectNothingOfExample('export', pkg.manifest);
    expectNothingOfExample('export', pkg.files);
    // The same export for example-bank holds its hooks.
    const own = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'flowable', tenant: 'example-bank' });
    expect(JSON.stringify(own.files)).toMatch(/b\.hook\./);
  });

  it('the assistant\'s tools read the same view as the user: a search, a read, the samples and the bench name nothing of another bank\'s', async () => {
    const turns = [
      { content: [
        { type: 'tool_use', id: 't1', name: 'search_catalogue', input: { query: 'b.' } },
        { type: 'tool_use', id: 't2', name: 'read_artefact', input: { name: 'b.validate.internal-limit.v1' } },
        { type: 'tool_use', id: 't3', name: 'list_samples', input: {} },
        { type: 'tool_use', id: 't4', name: 'run_bench', input: { target: 'b.validate.internal-limit.v1' } },
      ] },
      { content: [{ type: 'text', text: 'Done.' }] },
    ];
    fs.writeFileSync(replayFile, JSON.stringify(turns));
    const before = process.env.TH_AI_REPLAY;
    process.env.TH_AI_REPLAY = replayFile;
    try {
      const r = await as('second.maker', 'POST', '/api/ai/agent', { context: { surface: 'general' }, messages: [{ role: 'user', content: 'Show me the bank blocks' }] });
      expect(r.status, JSON.stringify(r.body).slice(0, 300)).toBe(200);
      const results = r.body.messages.flatMap((m: any) => (Array.isArray(m.content) ? m.content : [])).filter((b: any) => b.type === 'tool_result');
      expect(results.length).toBe(4);
      expectNothingOfExample('assistant tools', results, turns);
      expect(results[1].content).toMatch(/No published/);
    } finally {
      if (before === undefined) delete process.env.TH_AI_REPLAY;
      else process.env.TH_AI_REPLAY = before;
    }
  });
});
