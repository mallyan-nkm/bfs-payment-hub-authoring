import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { parseYaml } from '../core/yaml.js';
import { walkYaml } from '../core/store.js';
import { unzip } from '../export/zip.js';
import { startEntryFor } from '../testing/runner.js';
import { runTable } from '../rules/service.js';
import { routeOf } from '../interp/interpreter.js';
import { blockedByWindows } from './blocked.js';

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

/** The engines the deploy runner drives. */
export type Engine = 'dtfx' | 'elsa' | 'temporal' | 'flowable';

export interface DeployRequest {
  packagePath: string; // a .zip from the studio's export, or the folder it unpacks to
  engine?: Engine;
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
  workers: { queue: string; port: number; pid: number; tasks: string[]; served: Record<string, number>; refused: number; valuesRead?: number }[];
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
  // The package's binding says which engine runs it; the deploy runner drives DTFx, Elsa, Temporal and
  // Flowable (Temporal and Flowable through the runtime's harnesses, runtime/tools/TransactHub.*Harness).
  const engine = binding.target as Engine;
  if (!['dtfx', 'elsa', 'temporal', 'flowable'].includes(engine)) throw new Error(`This package is for ${binding.target}; the deploy runner drives dtfx, elsa, temporal and flowable`);
  // Flowable runs as its own server (plan D3); the runtime's harness is its external worker over REST.
  if (engine === 'flowable' && !process.env.TH_FLOWABLE_URL) throw new Error('Flowable runs on the Flowable REST server: set TH_FLOWABLE_URL (for example http://127.0.0.1:8080/flowable-rest), and TH_FLOWABLE_USER and TH_FLOWABLE_PASSWORD if they are not the image defaults');
  if (req.engine && req.engine !== engine) throw new Error(`This package is for ${engine}, not ${req.engine}`);
  const log: string[] = [];
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'th-run-'));
  // The tenant's configuration set travels in the package (R-09-02); an environment may override it.
  const environment: DeployEnvironment = { ...req.environment, tenant: req.environment.tenant ?? manifest.tenant };
  const packaged = environment.tenant ? path.join(dir, 'configuration', `${environment.tenant}.yaml`) : undefined;
  if (!environment.configurationSet && packaged && fs.existsSync(packaged)) environment.configurationSet = parseYaml(fs.readFileSync(packaged, 'utf8'));
  if (environment.configurationSet) log.push(`configuration ${environment.tenant}@${environment.configurationSet.release}${req.environment.configurationSet ? '' : ' from the package'}`);
  if (process.env.TH_RUNTIME_URL) log.push(`values from the runtime at ${process.env.TH_RUNTIME_URL}, in force at each attempt's time`);
  const envFile = path.join(tmp, 'environment.json');
  fs.writeFileSync(envFile, JSON.stringify(environment));

  // 1. Workers: one process per queue.
  const procs: { queue: string; port: number; child: ChildProcess; url: string; tasks: string[] }[] = [];
  let port = req.basePort ?? 7400 + Math.floor(Math.random() * 400);
  try {
    for (const w of manifest.workers ?? []) {
      const wdir = path.join(dir, w.path);
      const p = port++;
      const child = spawn(process.execPath, [path.join(wdir, 'worker.cjs'), '--dir', wdir, '--port', String(p), '--env', envFile], {
        env: { PATH: process.env.PATH, TH_PY_HARNESS: path.join(wdir, 'py_harness.py'), NODE_PATH: path.join(req.studioRoot, 'node_modules'), ...(process.env.TH_WORKER_LOG ? { TH_WORKER_LOG: process.env.TH_WORKER_LOG } : {}), ...(process.env.TH_NATIVE_HOST ? { TH_NATIVE_HOST: process.env.TH_NATIVE_HOST } : {}), ...(process.env.TH_NATIVE_ACTIVITIES ? { TH_NATIVE_ACTIVITIES: process.env.TH_NATIVE_ACTIVITIES } : {}), ...(process.env.TH_RUNTIME_URL ? { TH_RUNTIME_URL: process.env.TH_RUNTIME_URL } : {}) },
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
    let endToEnd = false;
    let legs: Record<string, string> | undefined;
    const start = Date.parse(`${req.environment.evaluationDate}T06:00:00Z`);
    if (req.inbound) {
      const schemes = Object.values(documents).filter((d: any) => d.kind === 'scheme');
      const resolve = (n: string) => Object.entries(documents).find(([k]) => k.startsWith(`${n}@`))?.[1];
      const found = req.inbound.message
        ? (() => { for (const s of schemes) { const e = (s.messages ?? []).find((m: any) => m.id === req.inbound!.message); if (e) return { scheme: s, entry: e }; } return undefined; })()
        : startEntryFor(schemes, req.workflow, resolve) ?? (() => {
            const f = (manifest.fallbackStarts ?? []).find((x: any) => x.leg === req.workflow);
            const scheme = f && schemes.find((x: any) => x.scope === f.scheme);
            const entry = scheme && (scheme.messages ?? []).find((m: any) => m.id === f.message);
            return entry ? { scheme, entry } : undefined;
          })();
      const scheme = found?.scheme;
      const entry = found?.entry;
      if (!entry?.disposition?.start?.intakeMap) throw new Error(`No start message in the package's dispatch tables leads to ${req.workflow}`);
      const map = entry.disposition.start.intakeMap;
      const queue = binding.queues[scopeOf(map)];
      const msg = { id: 'in-1', type: entry.id, payload: req.inbound.payload };
      // End to end when the workflow is not the leg the message starts: the front door runs the
      // platform workflow that workflow-select picks, with that leg in its slot (R-05-08).
      endToEnd = entry.disposition.start.leg !== req.workflow;
      const direction = endToEnd ? (entry.direction === 'in' ? 'inward' : 'outward') : wf.direction ?? 'outward';
      ctx = { header: { conversation: 'conv-1', paymentId: 'P-1', scheme: scheme.scope, direction, receivedAt: new Date(start).toISOString(), ...(endToEnd ? routeOf(entry.disposition.start) : { kind: entry.disposition.start.kind }) }, messages: { inbound: { id: msg.id, type: msg.type } }, ...ctx };
      const r = await (await fetch(`${urlOf(queue)}/attempt`, { method: 'POST', body: JSON.stringify({ instance: 'intake', activity: map, step: 'intake', call: 1, attempt: 1, now: start, ctx, messages: [msg] }) })).json();
      if (r.kind !== 'result') throw new Error(`Intake ${map} failed: ${r.message ?? r.kind}`);
      for (const [p, v] of Object.entries<any>(r.writes ?? {})) setPath(ctx, p, v);
      intake = { activity: map, queue, outcome: r.outcome };
      log.push(`intake ${map} on ${queue}: ${r.outcome}`);
      if (r.outcome !== 'mapped') throw new Error(`Intake ${map} returned ${r.outcome}`);
      // The dispatch table routes, not the intake map (R-05-42): its values stand after the map.
      if (endToEnd) Object.assign(ctx.header, routeOf(entry.disposition.start), { scheme: scheme.scope });
      if (endToEnd) {
        const sel = Object.values<any>(documents).find((d) => d.name === 'p.table.workflow-select.v1');
        if (!sel) throw new Error('The package has no p.table.workflow-select.v1 for the front door');
        const chosen = runTable(sel, { Header: [ctx.header] }, { resolve: (n) => Object.values<any>(documents).find((d) => d.name === n), evaluationDate: req.environment.evaluationDate }).decisions.find((d) => d.name === 'workflow')?.value;
        log.push(`front door: workflow-select chose ${chosen ?? 'nothing'} for ${ctx.header.kind} ${ctx.header.direction} ${ctx.header.segment ?? ''}`.trimEnd());
        if (chosen !== req.workflow) throw new Error(`The front door chose ${chosen ?? 'no workflow'} for ${entry.id}, not ${req.workflow}`);
        const leg = Object.values<any>(documents).find((d) => d.name === entry.disposition.start.leg);
        if (!leg) throw new Error(`The leg ${entry.disposition.start.leg} that ${entry.id} starts is not in the package`);
        legs = { [leg.implements?.slot ?? 'scheme-leg']: entry.disposition.start.leg, ...(entry.disposition.start.refuseLeg ? { 'scheme-refuse': entry.disposition.start.refuseLeg } : {}) };
        log.push(`front door: ${entry.disposition.start.leg} fills ${Object.keys(legs).join(', ')}`);
      }
      // The payload stays with the workers by reference; every worker can resolve it.
      for (const pr of procs) if (pr.url !== urlOf(queue)) await fetch(`${pr.url}/attempt`, { method: 'POST', body: JSON.stringify({ instance: 'intake', activity: '__register__', step: 'intake', call: 1, attempt: 1, now: start, ctx, messages: [msg] }) });
    }

    // 4. The engine.
    // With a store (TH_STORE_PROVIDER), DTFx and Elsa run as the runtime builds them: the same harness code,
    // its claim check journalled to the database (plan N4). Temporal and Flowable always run as the runtime
    // builds them; Flowable deploys the package's BPMN to its REST server and works its jobs over REST.
    const runtimeTool = (name: string) => path.join(req.studioRoot, '..', 'runtime', 'tools', `TransactHub.${name}Harness`, 'bin', 'Release', 'net10.0', `TransactHub.${name}Harness.dll`);
    const onStore = !!process.env.TH_STORE_PROVIDER;
    const harnessDll = engine === 'temporal' || engine === 'flowable'
      ? runtimeTool(engine === 'temporal' ? 'Temporal' : 'Flowable')
      : onStore
        ? runtimeTool(engine === 'elsa' ? 'Elsa' : 'Dtfx')
        : engine === 'elsa'
          ? path.join(req.studioRoot, 'engines', 'elsa', 'harness', 'bin', 'Release', 'net8.0', 'ElsaHarness.dll')
          : path.join(req.studioRoot, 'engines', 'dtfx', 'harness', 'bin', 'Release', 'net8.0', 'DtfxHarness.dll');
    if (!fs.existsSync(harnessDll)) {
      const b = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: path.dirname(path.dirname(path.dirname(path.dirname(harnessDll)))), encoding: 'utf8' });
      if (b.status !== 0) throw new Error(`dotnet build failed: ${b.stdout}`);
    }
    const input = {
      studioRoot: req.studioRoot,
      chunkSize: binding.defaults?.forEachChunk ?? 500,
      built: { id: 'DEPLOY', spec: {}, workflow: req.workflow, ...(legs ? { legs } : {}), config: { evaluationDate: environment.evaluationDate, tenant: environment.tenant, bindings: environment.configurationSet?.bindings ?? [], values: [...(environment.configurationSet?.values ?? []), ...(environment.values ?? [])] }, context: ctx },
      documents,
      workers: Object.fromEntries(procs.map((p) => [p.queue, p.url])),
      queues: binding.queues,
      // The BPMN the studio exported for the Flowable REST server, deployed as it is (R-09-15).
      ...(engine === 'flowable' ? { bpmn: bpmnOf(dir) } : {}),
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
      throw new Error(err ? JSON.parse(err.slice(6)) : blockedByWindows(r.stderr, engine) ?? `The engine exited ${r.code}: ${r.stderr.slice(-2000)}`);
    }
    const obs = JSON.parse(obsLine.slice('OBSERVATION '.length));
    for (const l of r.stderr.split('\n').map((x) => x.trim()).filter(Boolean)) log.push(`[engine] ${l}`);
    // A runtime engine with a store (TH_STORE_PROVIDER): what the database holds after the run.
    const st = obs.engine?.store;
    if (st) log.push(`store: ${st.provider} holds ${st.bulks} bulk(s), ${st.payments} payment(s), ${st.parties} part(ies); ${st.payloads.length} payload(s) rebuilt from their facts, chains ${st.payloads.every((x: any) => x.chain) ? 'unbroken' : 'BROKEN'}, ${st.ok ? 'all match the run' : 'NOT the run'}`);
    const root = obs.instances?.[0] ?? {};
    // The other instances (a leg in its slot, a subflow), so a run can be read without the engine's own tools.
    for (const i of (obs.instances ?? []).slice(1)) if ((i.trace ?? []).length) log.push(`instance ${i.workflow ?? i.doc ?? i.id}: ${(i.trace ?? []).join(' | ')}`);
    const workers: DeployResult['workers'] = [];
    for (const pr of procs) {
      const st = await (await fetch(`${pr.url}/stats`)).json();
      workers.push({ queue: pr.queue, port: pr.port, pid: pr.child.pid ?? 0, tasks: pr.tasks, served: st.served, refused: st.refused, valuesRead: st.valuesRead ?? 0 });
    }
    return {
      workflow: req.workflow, release: wf.release, expandedFrom: wf.expandedFrom,
      outcome: (root.trace ?? []).slice(-1)[0]?.replace(/^end:/, ''), failed: root.failed,
      trace: root.trace ?? [], detail: root.detail ?? [], attempts: root.attempts ?? {},
      intake, workers, messages: obs.messages ?? [], engine: obs.engine, elapsedMs: Date.now() - t0, log,
    };
  } finally {
    // Stop the workers and wait for each to exit, so that none outlives the deploy.
    await Promise.all(procs.map((p) => new Promise<void>((done) => {
      if (p.child.exitCode !== null || p.child.signalCode !== null) return done();
      p.child.once('exit', () => done());
      p.child.kill();
    })));
    if (!req.keep) {
      fs.rmSync(tmp, { recursive: true, force: true });
      // The folder a zip was unpacked to is the runner's own; a folder the caller gave is not.
      if (dir !== req.packagePath) fs.rmSync(dir, { recursive: true, force: true });
    }
  }
}

/** The package's BPMN documents (bpmn/*.bpmn20.xml), by file name. */
function bpmnOf(dir: string): Record<string, string> {
  const d = path.join(dir, 'bpmn');
  if (!fs.existsSync(d)) throw new Error('The package has no bpmn/ folder: export it for flowable');
  return Object.fromEntries(fs.readdirSync(d).filter((f) => f.endsWith('.bpmn20.xml')).sort().map((f) => [f, fs.readFileSync(path.join(d, f), 'utf8')]));
}

function setPath(obj: any, p: string, v: any) {
  const parts = p.replace(/^items\[\]/, 'items').split('.');
  let cur = obj;
  for (let i = 0; i < parts.length - 1; i++) cur = cur[parts[i]] ??= {};
  const k = parts[parts.length - 1];
  // A write to a whole part merges into what the front door set (header.kind, scheme, paymentId).
  const plain = (x: any) => x && typeof x === 'object' && !Array.isArray(x);
  cur[k] = plain(cur[k]) && plain(v) ? { ...cur[k], ...v } : v;
}
