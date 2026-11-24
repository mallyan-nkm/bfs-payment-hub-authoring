import * as http from 'node:http';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { runTest } from '../src/testing/runner.js';
import { clone, freshEnv } from './helpers.js';

// A port the bench gives to the runtime (R-08-15): the builtin activities on it run in the runtime's native host, which
// answers through the bank's port binding, not a simulator. Here the native host is a stand-in that records each attempt.

const attempts: any[] = [];
let host: http.Server;
let before: string | undefined;

beforeAll(async () => {
  host = http.createServer(async (req, res) => {
    let raw = '';
    for await (const c of req) raw += c;
    const b = JSON.parse(raw);
    attempts.push(b);
    const outcome = b.activity === 'p.post.accounting.v1' ? 'posted' : 'reserved';
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ kind: 'result', outcome, writes: { 'facts.posting': { outcome, adapter: 'simulator' } } }));
  });
  await new Promise<void>((r) => host.listen(0, '127.0.0.1', () => r()));
  before = process.env.TH_NATIVE_HOST;
  process.env.TH_NATIVE_HOST = `http://127.0.0.1:${(host.address() as any).port}`;
});
afterAll(() => {
  if (before === undefined) delete process.env.TH_NATIVE_HOST;
  else process.env.TH_NATIVE_HOST = before;
  host.close();
});

describe('a port bound to the runtime (R-08-15)', () => {
  it('sends each credit of a payment file to the native host, with the tenant, the level and the template', async () => {
    const env = freshEnv();
    const t = clone(env.store.test('WPS-IN-001')!.doc);
    t.bench = t.bench.map((b: any) => (b.port === 'p.port.core-banking.v1' ? { port: b.port, simulator: 'runtime', scenario: '' } : b));
    const r = await runTest(env, t);
    expect(r.passed, JSON.stringify(r.checks?.filter((c: any) => !c.passed))).toBe(true);
    const credits = attempts.filter((a) => a.activity === 'p.post.accounting.v1');
    expect(credits.length).toBeGreaterThan(0);
    for (const c of credits) {
      expect(c.tenant).toBe('example-bank');
      expect(c.level).toBe('item');
      expect(c.params.template).toBe('default');
      expect(c.view.payment.creditorAccount.iban).toMatch(/^AE\d{21}$/);
    }
  });

  it('leaves a port the bench gives a simulator with the simulator', async () => {
    attempts.length = 0;
    const env = freshEnv();
    const r = await runTest(env, env.store.test('WPS-IN-001')!.doc);
    expect(r.passed).toBe(true);
    expect(attempts).toEqual([]);
  });
});

describe('every activity of a port the bench gives to the runtime has a native code-behind (R-09-213, Issue I-268)', () => {
  it('finds none missing for the laptop bench, which gives the WPS port to the runtime, and names one that is', async () => {
    const { runtimePortGaps } = await import('../src/checks/runtime-ports.js');
    const fs = await import('node:fs');
    const compose = fs.readFileSync(new URL('../../deploy/laptop/compose.yaml', import.meta.url), 'utf8');
    const bench = JSON.parse(/TH_WORKERS_BENCH: \$\{TH_LAPTOP_WORKERS_BENCH:-(\[.*\])\}/.exec(compose)![1]);
    expect(bench).toContainEqual({ port: 'uae.wps.port.wps.v1', simulator: 'runtime' });
    const docs = freshEnv().store.current().map((e) => e.doc);
    expect(runtimePortGaps(docs, bench)).toEqual([]);
    const without = clone(docs);
    delete without.find((d: any) => d.name === 'uae.wps.exchange.send-receipt-confirmation.v2').implementation.native;
    expect(runtimePortGaps(without, bench)).toEqual([{ port: 'uae.wps.port.wps.v1', activity: 'uae.wps.exchange.send-receipt-confirmation.v2', release: '2.0.0' }]);
  });

  it('counts no builtin activity: the runtime answers a builtin on a port given to it (R-08-15), as the holds on the operations queue', async () => {
    const { runtimePortGaps } = await import('../src/checks/runtime-ports.js');
    const docs = freshEnv().store.current().map((e) => e.doc);
    const holds = docs.filter((d: any) => d.kind === 'activity' && (d.ports ?? []).includes('p.port.operations-queue.v1'));
    expect(holds.map((d: any) => d.name)).toEqual(expect.arrayContaining(['p.hold.approval.v1', 'p.hold.repair.v1']));
    expect(holds.every((d: any) => d.implementation?.binding === 'builtin')).toBe(true);
    expect(runtimePortGaps(docs, [{ port: 'p.port.operations-queue.v1', simulator: 'runtime' }])).toEqual([]);
    // A script-behind on that port with no native code-behind is still a gap.
    const scripted = clone(docs);
    scripted.find((d: any) => d.name === 'p.hold.approval.v1').implementation = { binding: 'script', language: 'typescript', script: 'scripts/p.hold.approval.v1.ts' };
    expect(runtimePortGaps(scripted, [{ port: 'p.port.operations-queue.v1', simulator: 'runtime' }]).map((g) => g.activity)).toContain('p.hold.approval.v1');
  });
});
