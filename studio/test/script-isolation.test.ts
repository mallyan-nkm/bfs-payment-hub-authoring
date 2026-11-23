import { spawn } from 'node:child_process';
import * as net from 'node:net';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { isolation, scriptWorkerSpawn } from '../src/interp/isolation.js';
import { pythonSpawn, runPythonScript } from '../src/interp/pysandbox.js';
import { runScript } from '../src/interp/sandbox.js';

// R-08-20, Issue I-119: a script-behind runs in a worker process with nothing of the studio in it. These tests give the studio
// a synthetic secret in its environment and a file of its own, then try to reach both from a script, and from a stand-in for a
// script that has already broken out of its context (a probe started exactly as the worker is).

const SECRET = 'synthetic-studio-secret-for-the-isolation-test';
const studioFile = path.join(__dirname, '..', 'package.json');
const fixtures = path.join(__dirname, 'fixtures', 'isolation');
const api = { ports: { request: async () => ({ status: 'ok' }) }, rules: { run: () => ({ worst: 'pass', findings: [] }) }, params: {}, clock: { now: () => '2026-11-23T06:00:00.000Z', today: () => '2026-11-23' }, ids: { next: () => 'id-1' }, log: { info: () => {} } };

let server: net.Server;
let port = 0;
let accepted = 0;
beforeAll(async () => {
  process.env.TH_GITHUB_TOKEN = SECRET;
  process.env.TH_ISOLATION_TEST_SECRET = SECRET;
  server = net.createServer((s) => {
    accepted++;
    s.destroy();
  });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', () => r()));
  port = (server.address() as net.AddressInfo).port;
});
afterAll(() => {
  delete process.env.TH_ISOLATION_TEST_SECRET;
  server.close();
});

function run(spec: { command: string; args: string[]; env: Record<string, string>; cwd: string }, input?: string): Promise<any> {
  return new Promise((resolve, reject) => {
    const c = spawn(spec.command, spec.args, { env: spec.env, cwd: spec.cwd, stdio: ['pipe', 'pipe', 'pipe'] });
    let out = '';
    let err = '';
    c.stdout.on('data', (d) => (out += d));
    c.stderr.on('data', (d) => (err += d));
    c.on('error', reject);
    c.on('exit', () => {
      try {
        resolve(JSON.parse(out.trim().split('\n').pop()!));
      } catch {
        reject(new Error(`the probe wrote no report: ${out} ${err}`));
      }
    });
    c.stdin.end(input ?? '');
  });
}

describe('a script-behind runs in a worker with nothing of the studio in it (R-08-20)', () => {
  it('a TypeScript script reaches neither the studio\'s environment nor its files', async () => {
    const tries = [
      `typeof process !== 'undefined' && process.env.TH_GITHUB_TOKEN`,
      `Object.constructor('return typeof process !== "undefined" && process.env.TH_GITHUB_TOKEN')()`,
      `clock.now.constructor('return typeof process !== "undefined" && process.env.TH_GITHUB_TOKEN')()`,
      `(() => { try { return require('fs').readFileSync(${JSON.stringify(studioFile)}, 'utf8'); } catch { return false; } })()`,
    ];
    for (const t of tries) {
      const src = `export async function run({ clock }) { let v; try { v = ${t}; } catch (e) { v = false; } return v ? 'reached:' + String(v).slice(0, 80) : 'refused'; }`;
      const r = await runScript(src, 'reach.ts', {}, [], [], api as any).catch(() => ({ outcome: 'refused' }));
      expect(r.outcome, t).toBe('refused');
    }
  });

  it('a Python script that climbs to os finds an empty environment and no file', async () => {
    const climb = "[c for c in ().__class__.__base__.__subclasses__() if c.__name__ == '_wrap_close'][0].__init__.__globals__";
    const env = await runPythonScript(`def run(api):\n    g = ${climb}\n    return 'keys:' + ','.join(sorted(g['environ'].keys()))\n`, 'env.py', {}, [], [], api as any);
    // Python itself may set LC_CTYPE when it coerces the C locale (PEP 538); nothing of the studio's is there.
    expect(env.outcome.replace(/^keys:/, '').split(',').filter((k) => k && k !== 'LC_CTYPE')).toEqual([]);
    const file = await runPythonScript(`def run(api):\n    g = ${climb}\n    return 'read' if g['open'](${JSON.stringify(studioFile)}, 0) >= 0 else 'x'\n`, 'file.py', {}, [], [], api as any).catch(() => ({ outcome: 'refused' }));
    expect(file.outcome).toBe('refused');
  });

  it('a TypeScript worker that is broken out of has no environment, no file, no process and no network', async () => {
    const probe = scriptWorkerSpawn(path.join(fixtures, 'escape-probe.cjs'));
    probe.args.push(studioFile, String(process.pid), String(port));
    const r = await run(probe);
    expect(r.env).toEqual([]);
    expect(r.readStudioFile.reached).toBe(false);
    expect(r.readParentEnviron.reached).toBe(false);
    expect(r.writeFile.reached).toBe(false);
    expect(r.childProcess.reached).toBe(false);
    expect(r.workerThread.reached).toBe(false);
    if (isolation().network.startsWith('none')) expect(r.network.reached).toBe(false);
  });

  it.runIf(process.platform === 'linux')('a Python process past its audit hook has no environment, no file and no network', async () => {
    const before = accepted;
    const r = await run(pythonSpawn(path.join(fixtures, 'escape_probe.py')), JSON.stringify({ harness: path.join(__dirname, '..', 'src', 'interp', 'py_harness.py'), studioFile, ppid: process.pid, port }) + '\n');
    expect(r.env.filter((k: string) => k !== 'LC_CTYPE')).toEqual([]);
    expect(r.landlock).toBeGreaterThan(0);
    expect(r.readStudioFile.reached).toBe(false);
    expect(r.readParentEnviron.reached).toBe(false);
    expect(r.writeFile.reached).toBe(false);
    expect(r.listRoot.reached).toBe(false);
    expect(r.network.reached).toBe(false);
    expect(accepted).toBe(before);
  });

  it('bounds a script\'s memory: a TypeScript script that fills its heap stops its worker, not the studio', async () => {
    const src = `export async function run() { const a = []; for (let i = 0; i < 1e9; i++) a.push(new Array(1e5).fill(i)); return 'ok'; }`;
    await expect(runScript(src, 'mem.ts', {}, [], [], api as any, 60000)).rejects.toThrow(/memory|stopped/);
    // The next script runs in a fresh worker.
    expect((await runScript(`export async function run() { return 'ok'; }`, 'ok.ts', {}, [], [], api as any)).outcome).toBe('ok');
  });

  it('keeps one bank\'s scripts out of a worker another bank\'s ran in', async () => {
    // A script leaves a mark on its realm's global object; a later run, in a fresh context, never sees it, and a worker of another partition is another process.
    const mark = `export async function run() { globalThis.__mark = 'bank-a'; return 'ok'; }`;
    const look = `export async function run() { return typeof globalThis.__mark === 'undefined' ? 'clean' : 'seen'; }`;
    await runScript(mark, 'mark.ts', {}, [], [], api as any, 1000, 'bank-a');
    expect((await runScript(look, 'look.ts', {}, [], [], api as any, 1000, 'bank-b')).outcome).toBe('clean');
    expect((await runScript(look, 'look.ts', {}, [], [], api as any, 1000, 'bank-a')).outcome).toBe('clean');
  });
});
