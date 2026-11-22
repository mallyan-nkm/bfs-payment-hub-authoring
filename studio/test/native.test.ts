import * as http from 'node:http';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { runNative } from '../src/interp/interpreter.js';
import { ActivityFailure, TransientFailure } from '../src/interp/reference.js';
import { ContractViolation } from '../src/interp/sandbox.js';

// A native code-behind runs in the runtime's native host, never in the studio (R-08-14). The studio's
// interpreter, and every deployed worker (which runs the same code), send the attempt over the native protocol:
// POST /native at TH_NATIVE_HOST, with the view, the parameter values and the engine's time. The outcome must
// be one of the activity's, the writes within its declared writes. Without a host the attempt fails with
// NativeHostUnavailable. Here the host is a stand-in that answers what each test asks of it.
// The runtime's side, the host and its code-behind, is runtime/tests/TransactHub.Store.Tests/NativeActivityTests.cs.

const ACT = {
  kind: 'activity', name: 'uae.wps.map.native-test.v1', release: '1.0.0', implementation: { binding: 'native' },
  reads: ['payment.instructedAmount'], writes: ['facts.file'], outcomes: [{ name: 'mapped', primary: true }, { name: 'refused' }],
};
const VIEW = { payment: { instructedAmount: { value: 10, currency: 'AED' } } };
const NOW = '2026-11-17T06:00:00.000Z';
const received: any[] = [];
let reply: any = {};
let host: http.Server;
let url = '';
let before: string | undefined;

beforeAll(async () => {
  host = http.createServer(async (req, res) => {
    let raw = '';
    for await (const c of req) raw += c;
    received.push({ method: req.method, path: req.url, body: JSON.parse(raw) });
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify(reply));
  });
  await new Promise<void>((r) => host.listen(0, '127.0.0.1', () => r()));
  url = `http://127.0.0.1:${(host.address() as any).port}`;
  before = process.env.TH_NATIVE_HOST;
});
afterAll(() => {
  if (before === undefined) delete process.env.TH_NATIVE_HOST;
  else process.env.TH_NATIVE_HOST = before;
  host.close();
});

async function failure(p: Promise<unknown>) {
  try {
    await p;
  } catch (e) {
    return e;
  }
  throw new Error('expected the attempt to fail');
}

describe('native code-behind (R-08-14)', () => {
  it('without a native host the attempt fails with NativeHostUnavailable, never with a guessed result', async () => {
    delete process.env.TH_NATIVE_HOST;
    const e = await failure(runNative(ACT, VIEW, {}, NOW));
    expect(e).toBeInstanceOf(ActivityFailure);
    expect((e as ActivityFailure).errorName).toBe('NativeHostUnavailable');
  });

  it('sends the attempt to POST /native with the view, the parameters, the time, the tenant and the level; takes the outcome and the writes', async () => {
    process.env.TH_NATIVE_HOST = `${url}/`;
    received.length = 0;
    reply = { kind: 'result', outcome: 'mapped', writes: { 'facts.file': { name: 'synthetic.SIF' } } };
    const r = await runNative(ACT, VIEW, { template: 'default' }, NOW, 'example-bank', 'item');
    expect(r).toEqual({ outcome: 'mapped', reasonCode: undefined, writes: { 'facts.file': { name: 'synthetic.SIF' } } });
    expect(received).toEqual([{ method: 'POST', path: '/native', body: { activity: ACT.name, release: '1.0.0', view: VIEW, params: { template: 'default' }, now: NOW, tenant: 'example-bank', level: 'item' } }]);
  });

  it('refuses an outcome the activity does not declare', async () => {
    process.env.TH_NATIVE_HOST = url;
    reply = { kind: 'result', outcome: 'guessed', writes: {} };
    const e = await failure(runNative(ACT, VIEW, {}, NOW));
    expect(e).toBeInstanceOf(ContractViolation);
    expect((e as Error).message).toMatch(/returned guessed, which is not one of its outcomes/);
  });

  it('refuses a write outside the declared writes', async () => {
    process.env.TH_NATIVE_HOST = url;
    reply = { kind: 'result', outcome: 'mapped', writes: { 'facts.file': {}, 'payment.instructedAmount.value': 0 } };
    const e = await failure(runNative(ACT, VIEW, {}, NOW));
    expect(e).toBeInstanceOf(ContractViolation);
    expect((e as ContractViolation).code).toBe('R-08-14');
    expect((e as Error).message).toMatch(/wrote payment\.instructedAmount\.value, outside its declared writes/);
  });

  it('passes on the host\'s error, and treats a host that cannot be reached as a transient failure', async () => {
    process.env.TH_NATIVE_HOST = url;
    reply = { kind: 'error', name: 'RoutingCodeMissing', message: 'no routing code', retryable: false };
    const e = await failure(runNative(ACT, VIEW, {}, NOW));
    expect(e).toBeInstanceOf(ActivityFailure);
    expect((e as ActivityFailure).errorName).toBe('RoutingCodeMissing');
    process.env.TH_NATIVE_HOST = 'http://127.0.0.1:9';
    const t = await failure(runNative(ACT, VIEW, {}, NOW));
    expect(t).toBeInstanceOf(TransientFailure);
    expect((t as TransientFailure).errorName).toBe('NativeHostUnreachable');
  });
});
