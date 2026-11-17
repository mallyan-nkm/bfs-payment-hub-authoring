import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { parseYaml } from '../core/yaml.js';
import { walkYaml } from '../core/store.js';
import { unzip } from '../export/zip.js';
import { startEntryFor } from '../testing/runner.js';

// Deploy an export package and run it (Part 9). The package's workers start as separate processes,
// one per task queue, each serving only its own tasks. The engine harness runs the exported, expanded
// workflow and sends every activity attempt to the worker of that task's queue. The front door runs
// the scheme's intake map on its worker first. Nothing here reads the studio's store: the package is
// the whole deployment.

export interface DeployEnvironment {
  evaluationDate: string;
  tenant?: string;
  bench?: { port: string; simulator: string; scenario: string }[];
  values?: any[];
  configurationSet?: any;
}

export interface DeployRequest {
  packagePath: string; // a .zip from the studio's export, or the folder it unpacks to
  engine?: 'dtfx';
  workflow: string;
  environment: DeployEnvironment;
  inbound?: { message?: string; payload: string };
  context?: any;
  basePort?: number;
  studioRoot: string; // where the engine harnesses live
  keep?: boolean;
}

export interface DeployResult {
  workflow: string;
  release: string;
  expandedFrom?: any;
  outcome?: string;
  failed?: string;
  trace: string[];
  detail: string[];
  attempts: Record<string, number>;
  intake?: { activity: string; queue: string; outcome: string };
  workers: { queue: string; port: number; pid: number; tasks: string[]; served: Record<string, number>; refused: number }[];
  messages: any[];
  engine: any;
  elapsedMs?: number;
  log: string[];
}

function unpack(p: string): string {
  if (fs.statSync(p).isDirectory()) return p;
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'th-deploy-'));
  for (const e of unzip(fs.readFileSync(p))) {
    const f = path.join(dir, e.name);
    fs.mkdirSync(path.dirname(f), { recursive: true });
    fs.writeFileSync(f, e.content);
  }
  return dir;
}

async function waitHealthy(url: string, ms = 15000) {
  const until = Date.now() + ms;
  for (;;) {
    try {
      const r = await fetch(`${url}/health`);
      if (r.ok) return r.json();
    } catch {
      // not up yet
    }
    if (Date.now() > until) throw new Error(`Worker at ${url} did not start`);
    await new Promise((r) => setTimeout(r, 100));
  }
}

function scopeOf(name: string) {
  const parts = name.split('.');
  return parts[0] === 'p' || parts[0] === 'b' ? parts[0] : parts.slice(0, 2).join('.');
}

export async function deploy(req: DeployRequest): Promise<DeployResult> {
  const dir = unpack(req.packagePath);
  const manifest = parseYaml(fs.readFileSync(path.join(dir, 'manifest.yaml'), 'utf8'));
  const binding = parseYaml(fs.readFileSync(path.join(dir, 'binding.yaml'), 'utf8'));
  if ((req.engine ?? 'dtfx') !== 'dtfx' || binding.target !== 'dtfx') throw new Error(`This package is for ${binding.target}; the deploy runner drives dtfx`);
  const log: string[] = [];
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'th-run-'));
  const envFile = path.join(tmp, 'environment.json');
  fs.writeFileSync(envFile, JSON.stringify(req.environment));

  // 1. Workers: one process per queue.
  const procs: { queue: string; port: number; child: ChildProcess; url: string; tasks: string[] }[] = [];
  let port = req.basePort ?? 7400 + Math.floor(Math.random() * 400);
  try {
    for (const w of manifest.workers ?? []) {
      const wdir = path.join(dir, w.path);
      const p = port++;
      const child = spawn(process.execPath, [path.join(wdir, 'worker.cjs'), '--dir', wdir, '--port', String(p), '--env', envFile], {
        env: { PATH: process.env.PATH, TH_PY_HARNESS: path.join(wdir, 'py_harness.py'), NODE_PATH: path.join(req.studioRoot, 'node_modules') },
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      child.stderr?.on('data', (d) => log.push(`[${w.queue}] ${String(d).trim()}`));
      procs.push({ queue: w.queue, port: p, child, url: `http://127.0.0.1:${p}`, tasks: w.tasks });
    }
    for (const pr of procs) {
      const h = await waitHealthy(pr.url);
      log.push(`worker ${h.queue} up on ${pr.port} with ${h.tasks.length} task(s), pid ${pr.child.pid}`);
    }
    const urlOf = (queue: string) => procs.find((p) => p.queue === queue)?.url;

    // 2. The documents an engine receives: everything in the package but the workers and tests.
    const documents: Record<string, any> = {};
    for (const sub of ['workflows', 'activities', 'rules', 'ports', 'extensions', 'hooks', 'schemes']) {
      const d = path.join(dir, sub);
      if (!fs.existsSync(d)) continue;
      for (const f of walkYaml(d)) {
        const doc = parseYaml(fs.readFileSync(f, 'utf8'));
        documents[`${doc.name ?? doc.scope}@${doc.release}`] = doc;
      }
    }
    const wfKey = Object.keys(documents).find((k) => k.startsWith(`${req.workflow}@`));
    if (!wfKey) throw new Error(`${req.workflow} is not in the package`);
    const wf = documents[wfKey];

    // 3. The front door: the scheme's intake map, on the worker of its queue.
    let ctx: any = req.context ?? {};
    let intake: DeployResult['intake'];
    const start = Date.parse(`${req.environment.evaluationDate}T06:00:00Z`);
    if (req.inbound) {
      const schemes = Object.values(documents).filter((d: any) => d.kind === 'scheme');
      const resolve = (n: string) => Object.entries(documents).find(([k]) => k.startsWith(`${n}@`))?.[1];
      const found = req.inbound.message
        ? (() => { for (const s of schemes) { const e = (s.messages ?? []).find((m: any) => m.id === req.inbound!.message); if (e) return { scheme: s, entry: e }; } return undefined; })()
        : startEntryFor(schemes, req.workflow, resolve);
      const scheme = found?.scheme;
      const entry = found?.entry;
      if (!entry?.disposition?.start?.intakeMap) throw new Error(`No start message in the package's dispatch tables leads to ${req.workflow}`);
      const map = entry.disposition.start.intakeMap;
      const queue = binding.queues[scopeOf(map)];
      const msg = { id: 'in-1', type: entry.id, payload: req.inbound.payload };
      ctx = { header: { conversation: 'conv-1', paymentId: 'P-1', scheme: scheme.scope, kind: entry.disposition.start.kind, direction: wf.direction ?? 'outward', receivedAt: new Date(start).toISOString() }, messages: { inbound: { id: msg.id, type: msg.type } }, ...ctx };
      const r = await (await fetch(`${urlOf(queue)}/attempt`, { method: 'POST', body: JSON.stringify({ instance: 'intake', activity: map, step: 'intake', call: 1, attempt: 1, now: start, ctx, messages: [msg] }) })).json();
      if (r.kind !== 'result') throw new Error(`Intake ${map} failed: ${r.message ?? r.kind}`);
      for (const [p, v] of Object.entries<any>(r.writes ?? {})) setPath(ctx, p, v);
      intake = { activity: map, queue, outcome: r.outcome };
      log.push(`intake ${map} on ${queue}: ${r.outcome}`);
      if (r.outcome !== 'mapped') throw new Error(`Intake ${map} returned ${r.outcome}`);
      // The payload stays with the workers by reference; every worker can resolve it.
      for (const pr of procs) if (pr.url !== urlOf(queue)) await fetch(`${pr.url}/attempt`, { method: 'POST', body: JSON.stringify({ instance: 'intake', activity: '__register__', step: 'intake', call: 1, attempt: 1, now: start, ctx, messages: [msg] }) });
    }

    // 4. The engine.
    const harnessDll = path.join(req.studioRoot, 'engines', 'dtfx', 'harness', 'bin', 'Release', 'net8.0', 'DtfxHarness.dll');
    if (!fs.existsSync(harnessDll)) {
      const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: path.dirname(path.dirname(path.dirname(path.dirname(harnessDll)))), encoding: 'utf8' });
      if (b.status !== 0) throw new Error(`dotnet build failed: ${b.stdout}`);
    }
    const input = {
      studioRoot: req.studioRoot,
      chunkSize: binding.defaults?.forEachChunk ?? 500,
      built: { id: 'DEPLOY', spec: {}, workflow: req.workflow, config: { evaluationDate: req.environment.evaluationDate, tenant: req.environment.tenant, bindings: req.environment.configurationSet?.bindings ?? [] }, context: ctx },
      documents,
      workers: Object.fromEntries(procs.map((p) => [p.queue, p.url])),
      queues: binding.queues,
    };
    const inFile = path.join(tmp, 'input.json');
    fs.writeFileSync(inFile, JSON.stringify(input));
    const t0 = Date.now();
    const r = await new Promise<{ stdout: string; stderr: string; code: number | null }>((resolve) => {
      const child = spawn('dotnet', [harnessDll, inFile], { stdio: ['ignore', 'pipe', 'pipe'] });
      let stdout = '';
      let stderr = '';
      child.stdout.on('data', (d) => (stdout += d));
      child.stderr.on('data', (d) => (stderr += d));
      child.on('close', (code) => resolve({ stdout, stderr, code }));
    });
    const lines = r.stdout.split('\n');
    const obsLine = lines.find((l) => l.startsWith('OBSERVATION '));
    if (!obsLine) {
      const err = lines.find((l) => l.startsWith('ERROR '));
      throw new Error(err ? JSON.parse(err.slice(6)) : `The engine exited ${r.code}: ${r.stderr.slice(-2000)}`);
    }
    const obs = JSON.parse(obsLine.slice('OBSERVATION '.length));
    const root = obs.instances?.[0] ?? {};
    const workers: DeployResult['workers'] = [];
    for (const pr of procs) {
      const st = await (await fetch(`${pr.url}/stats`)).json();
      workers.push({ queue: pr.queue, port: pr.port, pid: pr.child.pid ?? 0, tasks: pr.tasks, served: st.served, refused: st.refused });
    }
    return {
      workflow: req.workflow, release: wf.release, expandedFrom: wf.expandedFrom,
      outcome: (root.trace ?? []).slice(-1)[0]?.replace(/^end:/, ''), failed: root.failed,
      trace: root.trace ?? [], detail: root.detail ?? [], attempts: root.attempts ?? {},
      intake, workers, messages: obs.messages ?? [], engine: obs.engine, elapsedMs: Date.now() - t0, log,
    };
  } finally {
    for (const p of procs) p.child.kill();
    if (!req.keep) fs.rmSync(tmp, { recursive: true, force: true });
  }
}

function setPath(obj: any, p: string, v: any) {
  const parts = p.replace(/^items\[\]/, 'items').split('.');
  let cur = obj;
  for (let i = 0; i < parts.length - 1; i++) cur = cur[parts[i]] ??= {};
  cur[parts[parts.length - 1]] = v;
}
