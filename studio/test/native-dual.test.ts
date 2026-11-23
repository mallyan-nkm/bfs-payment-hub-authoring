import * as http from 'node:http';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { nativeHere } from '../src/interp/interpreter.js';
import { runTest } from '../src/testing/runner.js';
import { freshEnv } from './helpers.js';

// An activity the runtime's native host implements as well (R-08-16): the studio runs its script-behind or builtin, unless the
// run gives the activity to the runtime (TH_NATIVE_ACTIVITIES). Here the native host is a stand-in that records each attempt and
// answers mapped; the real host, with the source's parsers and builders, is in blackbox/e2e-wps-inward-native.test.ts.

const attempts: any[] = [];
let host: http.Server;
const saved = { host: process.env.TH_NATIVE_HOST, acts: process.env.TH_NATIVE_ACTIVITIES };

beforeAll(async () => {
  host = http.createServer(async (req, res) => {
    let raw = '';
    for await (const c of req) raw += c;
    const b = JSON.parse(raw);
    attempts.push(b);
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ kind: 'result', outcome: 'mapped', writes: { 'messages.rendered': { by: `${b.activity}@native` } } }));
  });
  await new Promise<void>((r) => host.listen(0, '127.0.0.1', () => r()));
  process.env.TH_NATIVE_HOST = `http://127.0.0.1:${(host.address() as any).port}`;
});
afterAll(() => {
  for (const [k, v] of [['TH_NATIVE_HOST', saved.host], ['TH_NATIVE_ACTIVITIES', saved.acts]] as const) {
    if (v === undefined) delete process.env[k];
    else process.env[k] = v;
  }
  host.close();
});

describe('a script-behind or builtin the native host implements too (R-08-16)', () => {
  it('names the WPS files the runtime implements, and no other activity', () => {
    const env = freshEnv();
    const native = env.store.current().filter((e) => e.kind === 'activity' && e.doc.implementation?.native).map((e) => e.name).sort();
    // The encryption and decryption blocks and the labour ministry lookup too (Issues I-117, I-118).
    // FX, charges and the posting that reads them too (R-12-25, Issue I-136).
    expect(native).toEqual(['p.decide.posting-window.v1', 'p.enrich.charges.v1', 'p.enrich.fx-rate.v1', 'p.post.accounting.v2', 'p.protect.decrypt-file.v1', 'p.protect.encrypt-file.v1', 'uae.wps.enrich.employer-status.v1', 'uae.wps.map.ack-in.v1', 'uae.wps.map.dif-out.v2', 'uae.wps.map.pif-in.v1', 'uae.wps.map.prc-out.v2', 'uae.wps.map.rfa-out.v1', 'uae.wps.map.rtc-in.v2']);
  });

  it('runs the script or the builtin unless the run gives the activity to the runtime', async () => {
    const env = freshEnv();
    const act = env.store.doc('uae.wps.map.prc-out.v2');
    delete process.env.TH_NATIVE_ACTIVITIES;
    expect(nativeHere(act)).toBe(false);
    const r = await runTest(env, env.store.test('WPS-IN-002')!.doc);
    expect(r.passed).toBe(true);
    expect(attempts).toEqual([]);
    process.env.TH_NATIVE_ACTIVITIES = 'uae.wps.map.prc-out.v2@2.0.0';
    expect(nativeHere(act)).toBe(true);
    expect(nativeHere(env.store.doc('uae.wps.map.dif-out.v2'))).toBe(false);
    expect(nativeHere(env.store.doc('uae.wps.map.sif-in.v1')), 'an activity that does not say so is never sent').toBe(false);
    // `*` gives every such activity to the runtime, and `!<name>` keeps one on the studio.
    process.env.TH_NATIVE_ACTIVITIES = '*,!p.protect.encrypt-file.v1';
    expect(nativeHere(act)).toBe(true);
    expect(nativeHere(env.store.doc('p.protect.encrypt-file.v1'))).toBe(false);
  });

  it('sends the attempt to the native host with its view, its parameters and the tenant, and keeps the trace', async () => {
    const env = freshEnv();
    process.env.TH_NATIVE_ACTIVITIES = 'uae.wps.map.prc-out.v2,uae.wps.map.dif-out.v2';
    const r = await runTest(env, env.store.test('WPS-IN-002')!.doc);
    expect(r.passed, JSON.stringify(r.checks?.filter((c: any) => !c.passed))).toBe(true);
    expect(attempts.map((a) => a.activity)).toEqual(['uae.wps.map.prc-out.v2', 'uae.wps.map.dif-out.v2']);
    expect(attempts[0]).toMatchObject({ release: '2.0.0', tenant: 'example-bank', params: { agentId: '000' } });
    expect(attempts[0].view.items).toHaveLength(2);
    delete process.env.TH_NATIVE_ACTIVITIES;
  });
});
