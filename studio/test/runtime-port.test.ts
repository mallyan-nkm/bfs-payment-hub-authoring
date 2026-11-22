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
