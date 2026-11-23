import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { spawn } from 'node:child_process';
import { afterEach, describe, expect, it } from 'vitest';
import { FolderSequences, SimulatorInstance } from '../src/sim/runtime.js';
import { VirtualClock } from '../src/interp/clock.js';
import { freshEnv } from './helpers.js';

// A simulator's scenario is a sequence over the payment, whichever worker plays it (Issue I-162, R-09-89): the deployment's
// workers share each scenario's place in one folder, per conversation.

const env = freshEnv();
const host = (clock: VirtualClock) => ({ clock, deliver: () => {}, newId: (p: string) => `${p}-${Math.random().toString(36).slice(2)}`, log: () => {} });
const post = (id: string, conversation = 'conv-1'): any => ({ id, type: 'p.core.request', port: 'p.port.core-banking.v1', from: 'platform', at: 0, correlation: {}, conversation });
const status = (r: any) => r.kind === 'reply' ? r.message.status : r.kind;

describe('a simulator scenario shared across workers (I-162)', () => {
  let dir = '';
  afterEach(() => {
    SimulatorInstance.shared = undefined;
    if (dir) fs.rmSync(dir, { recursive: true, force: true });
  });

  it('without a shared place, each worker plays the scenario from its start', () => {
    const sim = env.store.doc('p.sim.core-banking.v1');
    const credits = new SimulatorInstance(sim, 'reject-first-then-accept', host(new VirtualClock(0)));
    const returns = new SimulatorInstance(sim, 'reject-first-then-accept', host(new VirtualClock(0)));
    expect(status(credits.receive(post('m1'), true))).toBe('rejected');
    expect(status(returns.receive(post('m2'), true))).toBe('rejected'); // the I-162 symptom: the return is refused
  });

  it('with one, the second worker takes the scenario where the first left it, per conversation', () => {
    dir = fs.mkdtempSync(path.join(os.tmpdir(), 'th-sim-'));
    SimulatorInstance.shared = new FolderSequences(dir);
    const sim = env.store.doc('p.sim.core-banking.v1');
    const credits = new SimulatorInstance(sim, 'reject-first-then-accept', host(new VirtualClock(0)));
    const returns = new SimulatorInstance(sim, 'reject-first-then-accept', host(new VirtualClock(0)));
    expect(status(credits.receive(post('m1'), true))).toBe('rejected');
    expect(status(credits.receive(post('m2'), true))).toBe('accepted');
    expect(status(returns.receive(post('m3'), true))).toBe('accepted');
    // A retry of a message keeps its answer; another payment's conversation starts the scenario again.
    expect(status(credits.receive(post('m1'), true))).toBe('rejected');
    expect(status(returns.receive(post('m4', 'conv-2'), true))).toBe('rejected');
  });

  it('an inward simulator plays its leading sends once, whichever workers start it', () => {
    dir = fs.mkdtempSync(path.join(os.tmpdir(), 'th-sim-'));
    SimulatorInstance.shared = new FolderSequences(dir);
    const sim = env.store.doc('uae.wps.sim.wps-inward.v1');
    const sent: any[] = [];
    const h = (clock: VirtualClock) => ({ ...host(clock), deliver: (m: any) => sent.push(m) });
    const a = new SimulatorInstance(sim, 'payment-file', h(new VirtualClock(0)));
    const b = new SimulatorInstance(sim, 'payment-file', h(new VirtualClock(0)));
    a.start('conv-1');
    b.start('conv-1');
    expect(a.emitted.length).toBeGreaterThan(0);
    expect(b.emitted.length).toBe(0);
  });

  it('holds its lock across processes: every move is counted once', async () => {
    dir = fs.mkdtempSync(path.join(os.tmpdir(), 'th-sim-'));
    const script = path.join(dir, 'count.mjs');
    const mod = path.resolve('src/sim/runtime.ts');
    fs.writeFileSync(script, `const { FolderSequences } = await import(${JSON.stringify(mod)});\nconst s = new FolderSequences(${JSON.stringify(path.join(dir, 'seq'))});\nfor (let i = 0; i < 50; i++) s.withCursor('k', 0, (c) => ({ result: c, cursor: c + 1 }));\n`);
    const tsx = path.resolve('node_modules/.bin/tsx');
    // Three processes at once, as three workers.
    const runs = await Promise.all([0, 1, 2].map(() => new Promise<{ status: number | null; stderr: string }>((ok) => {
      const c = spawn(tsx, [script]);
      let stderr = '';
      c.stderr.on('data', (d) => (stderr += d));
      c.on('close', (status) => ok({ status, stderr }));
    })));
    for (const r of runs) expect(r.status, r.stderr).toBe(0);
    const s = new FolderSequences(path.join(dir, 'seq'));
    expect(s.withCursor('k', 0, (c) => ({ result: c, cursor: c }))).toBe(150);
  });
});
