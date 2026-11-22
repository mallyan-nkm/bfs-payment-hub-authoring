import { describe, expect, it } from 'vitest';
import { lintScript, runScript } from '../src/interp/sandbox.js';

const api = { ports: {}, rules: {}, params: {}, clock: { now: () => '2026-11-17T06:00:00.000Z', today: () => '2026-11-17' }, ids: { next: () => 'id-1' }, log: { info: () => {} } };

describe('the TypeScript script sandbox (C07)', () => {
  it('runs a script with the injected interface and returns its changes', async () => {
    const src = `export async function run({ ctx }) { ctx.facts = { x: { done: true } }; return 'ok'; }`;
    const r = await runScript(src, 'a.ts', { header: { paymentId: 'P-1' } }, ['header'], ['facts.x'], api as any);
    expect(r.outcome).toBe('ok');
    expect(r.changes.map((c) => c.path)).toEqual(['facts.x.done']);
  });
  it('fails a write outside the declared writes (R-03-07)', async () => {
    const src = `export async function run({ ctx }) { ctx.status = { code: 'X' }; return 'ok'; }`;
    await expect(runScript(src, 'b.ts', {}, ['header'], ['facts.x'], api as any)).rejects.toThrow(/not in its declared writes/);
  });
  it('fails a read outside the declared reads (R-03-07)', async () => {
    const src = `export async function run({ ctx }) { return ctx.payment ? 'a' : 'b'; }`;
    await expect(runScript(src, 'c.ts', { header: {} }, ['header'], [], api as any)).rejects.toThrow(/not in its declared reads/);
  });
  it('has no randomness and a virtual clock (C07-S3)', async () => {
    const rnd = `export async function run() { return String(Math.random()); }`;
    await expect(runScript(rnd, 'd.ts', {}, [], [], api as any)).rejects.toThrow(/Math.random/);
    const clk = `export async function run() { return new Date().toISOString() === '2026-11-17T06:00:00.000Z' ? 'ok' : 'bad'; }`;
    expect((await runScript(clk, 'e.ts', {}, [], [], api as any)).outcome).toBe('ok');
    const noReq = `export async function run() { return typeof require === 'undefined' && typeof process === 'undefined' ? 'ok' : 'bad'; }`;
    expect((await runScript(noReq, 'f.ts', {}, [], [], api as any)).outcome).toBe('ok');
  });
  it('lints a script that names a forbidden global (L28)', () => {
    expect(lintScript(`const x = Date.now(); fetch('x');`).length).toBe(2);
    expect(lintScript(`// Date.now() in a comment\nexport async function run() { return 'ok'; }`)).toEqual([]);
  });

  // R-08-18: nothing of the host's realm is in reach, so no constructor chain leads to the host's Function, process or require.
  it('gives a script no way to the host through a constructor chain', async () => {
    const tries = [
      `Object.constructor('return process')()`,
      `Array.constructor('return process')()`,
      `clock.now.constructor('return process')()`,
      `ids.next.constructor('return process')()`,
      `ctx.constructor.constructor('return process')()`,
      `(await (async () => {})()).constructor`,
      `ports.request.constructor('return process')()`,
      `rules.money.add.constructor('return process')()`,
      `(() => { try { ports.missing(); } catch (e) { return e.constructor.constructor('return process')(); } })()`,
      `(await ports.request('p.port.x.v1').catch((e) => e)).constructor.constructor('return process')()`,
      `this.constructor.constructor('return process')()`,
    ];
    const hostApi = { ...api, ports: { request: async () => { throw new Error('refused'); } }, rules: { money: { add: (...x: number[]) => x.reduce((a, b) => a + b, 0) } } };
    for (const t of tries) {
      const src = `export async function run({ ctx, ports, clock, ids, rules }) { let p; try { p = ${t}; } catch (e) { return 'refused'; } return p && typeof p.env === 'object' && typeof p.exit === 'function' ? 'escaped' : 'refused'; }`;
      const r = await runScript(src, 'escape.ts', { header: {} }, ['header'], [], hostApi as any).catch((e) => ({ outcome: 'refused', e }));
      expect(r.outcome, t).toBe('refused');
    }
  });
  it('bounds every slice of a script by its timeout, after an await too', async () => {
    const spin = `export async function run({ ports }) { await ports.request('p.port.x.v1'); for (;;) {} }`;
    const slow = { ...api, ports: { request: async () => ({ status: 'ok' }) } };
    await expect(runScript(spin, 'spin.ts', {}, [], [], slow as any, 200)).rejects.toThrow(/ran past 200 ms/);
    const top = `for (;;) {} export async function run() { return 'ok'; }`;
    await expect(runScript(top, 'top.ts', {}, [], [], api as any, 200)).rejects.toThrow(/ran past 200 ms/);
  });
  it('hands back an error the interface threw as it was, so the run sees the activity failure', async () => {
    class Refused extends Error {}
    const failing = { ...api, ports: { request: async () => { throw new Refused('core banking refused'); } } };
    const src = `export async function run({ ports }) { await ports.request('p.port.x.v1'); return 'ok'; }`;
    await expect(runScript(src, 'h.ts', {}, [], [], failing as any)).rejects.toBeInstanceOf(Refused);
  });
});
