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
});
