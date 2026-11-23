import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { parseYaml } from '../core/yaml.js';
import { walkYaml } from '../core/store.js';
import { unzip } from '../export/zip.js';
import { startEntryFor } from '../testing/runner.js';
import { selectWorkflow, startRoute } from './route.js';
import { routeOf } from '../interp/interpreter.js';
import { blockedByWindows } from './blocked.js';
import { attrs, childEnv, inSpan, SpanKind, traceHeaders } from '../telemetry/telemetry.js';
import { claimsOf, trustedSigners, verifyFiles, type Verification } from '../export/signing.js';
import { isSecretRef } from '../secrets/secrets.js';

// Deploy an export package and run it (Part 9). The package's workers start as separate processes,
// one per task queue, each serving only its own tasks. The engine harness runs the exported, expanded
// workflow and sends every activity attempt to the worker of that task's queue. The front door runs
// the scheme's intake map on its worker first. Nothing here reads the studio's store: the package is
// the whole deployment. A package is loaded only once its signature verifies against the signers this
// deployment trusts (TH_TRUSTED_SIGNERS, R-09-26): no worker starts and no engine sees it before.

export interface DeployEnvironment {
  evaluationDate: string;
  tenant?: string;
  bench?: { port: string; simulator: string; scenario: string }[];
  values?: any[];
  configurationSet?: any;
  /** When the run starts on its evaluation date, HH:MM:SS UTC (default 06:00:00): a bank's cut-off is a time of day (R-12-28). */
  startTime?: string;
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
  /** The first worker port, the next ones after it; by default each worker takes a free port the system gives it. */
  basePort?: number;
  studioRoot: string; // where the engine harnesses live
  keep?: boolean;
  /**
   * The runtime's load harness (runtime/load, R-09-38): many files through the same deployment at once, on Temporal.
   * Each file goes through the front door's intake on the workers, then the harness runs them all side by side.
   */
  load?: { inbounds: { label: string; payload: string }[]; concurrency: number; worker?: { maxConcurrentActivities?: number; maxConcurrentWorkflowTasks?: number } };
  /** The distribution the payment is pinned to (R-09-53): the engine runs it as that distribution's copy, and the store records it. */
  distribution?: string;
  /** The payment's id, as the front door accepted it. */
  paymentId?: string;
  /**
   * An installation's workers, shared by the bank's distributions (R-09-133): none is started or stopped for this run. Each attempt
   * names the payment's distribution, and the native host serves it from that distribution's build. Started with startWorkers.
   */
  workers?: WorkerSet;
}

/** A worker of a deployment: its queue, where it listens and its process, when this process started it. */
export interface WorkerProc { queue: string; port: number; url: string; tasks: string[]; restarts: number; child?: ChildProcess }

/** A package's workers, one process per task queue (R-09-04, R-09-05), and how to stop them. */
export interface WorkerSet { procs: WorkerProc[]; stop(): Promise<void> }

export interface StartWorkers {
  /** The unpacked engine package whose workers start. */
  packageDir: string;
  environment: DeployEnvironment;
  studioRoot: string;
  basePort?: number;
  /**
   * The distribution the workers run (TH_DISTRIBUTION): the native host refuses an attempt they send for another (R-09-131).
   * Without one the workers name none, and serve each attempt for the distribution it names (R-09-133).
   */
  distribution?: string;
  log?: string[];
  /** Where the environment file and the simulators' shared state go; by default a folder of the set's own, removed on stop. */
  workDir?: string;
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
  /** With req.load: what the harness measured, per run, and how long each intake took. */
  load?: any;
  /** The package's signature, as verified before anything ran (R-09-26). */
  verification: Verification;
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

/** The port a worker started with --port 0 listens on: its first line says so (`worker <queue> on <port>: ...`). */
function listening(child: ChildProcess, queue: string, ms = 15000): Promise<number> {
  return new Promise((resolve, reject) => {
    let out = '';
    const done = (f: () => void) => { clearTimeout(timer); child.stdout?.off('data', onData); child.off('exit', onExit); f(); };
    const onData = (d: Buffer) => {
      out += d;
      const m = /^worker \S+ on (\d+):/m.exec(out);
      if (m) done(() => resolve(Number(m[1])));
    };
    const onExit = (code: number | null) => done(() => reject(new Error(`Worker ${queue} exited ${code} before it listened: ${out.slice(-500)}`)));
    const timer = setTimeout(() => done(() => reject(new Error(`Worker ${queue} did not say which port it listens on`))), ms);
    child.stdout?.on('data', onData);
    child.on('exit', onExit);
  });
}

function scopeOf(name: string) {
  const parts = name.split('.');
  return parts[0] === 'p' || parts[0] === 'b' ? parts[0] : parts.slice(0, 2).join('.');
}

/** Every file of an unpacked package, by its path in the package (the branch's package.sha256 is not part of it). */
function packageFiles(dir: string): Record<string, Buffer> {
  const out: Record<string, Buffer> = {};
  const walk = (d: string) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const f = path.join(d, e.name);
      if (e.isDirectory()) walk(f);
      else out[path.relative(dir, f).split(path.sep).join('/')] = fs.readFileSync(f);
    }
  };
  walk(dir);
  delete out['package.sha256'];
  return out;
}

/** Verifies an unpacked package's signature against the deployment's trusted signers; throws when it does not verify. */
export async function verifyPackageDir(dir: string): Promise<Verification> {
  if (!fs.existsSync(path.join(dir, 'manifest.yaml'))) throw new Error('The package has no manifest.yaml');
  const manifest = parseYaml(fs.readFileSync(path.join(dir, 'manifest.yaml'), 'utf8'));
  const claims = claimsOf(manifest);
  return verifyFiles(packageFiles(dir), { releaseId: claims.releaseId, tenant: claims.tenant }, await trustedSigners());
}

/** One deployment's run, as a span (R-09-32): every child process continues this trace (TRACEPARENT, traceparent). */
export async function deploy(req: DeployRequest): Promise<DeployResult> {
  return inSpan('th.payment', attrs({ 'th.workflow': req.workflow, 'th.tenant': req.environment.tenant, 'th.engine': req.engine }), () => deployRun(req));
}

async function deployRun(req: DeployRequest): Promise<DeployResult> {
  const dir = unpack(req.packagePath);
  let verification: Verification;
  try {
    verification = await verifyPackageDir(dir);
  } catch (e: any) {
    if (dir !== req.packagePath) fs.rmSync(dir, { recursive: true, force: true });
    throw new Error(`The package is not deployed: ${e?.message ?? e}`);
  }
  const manifest = parseYaml(fs.readFileSync(path.join(dir, 'manifest.yaml'), 'utf8'));
  const binding = parseYaml(fs.readFileSync(path.join(dir, 'binding.yaml'), 'utf8'));
  // The package's binding says which engine runs it; the deploy runner drives DTFx, Elsa, Temporal and
  // Flowable (Temporal and Flowable through the runtime's harnesses, runtime/tools/TransactHub.*Harness).
  const engine = binding.target as Engine;
  if (!['dtfx', 'elsa', 'temporal', 'flowable'].includes(engine)) throw new Error(`This package is for ${binding.target}; the deploy runner drives dtfx, elsa, temporal and flowable`);
  // Flowable runs as its own server (plan D3); the runtime's harness is its external worker over REST.
  if (engine === 'flowable' && !process.env.TH_FLOWABLE_URL) throw new Error('Flowable runs on the Flowable REST server: set TH_FLOWABLE_URL (for example http://127.0.0.1:8080/flowable-rest), and TH_FLOWABLE_USER and TH_FLOWABLE_PASSWORD if they are not the image defaults');
  if (req.engine && req.engine !== engine) throw new Error(`This package is for ${engine}, not ${req.engine}`);
  const log: string[] = [`package verified: ${verification.files} file(s) signed by key ${verification.keyId}${verification.changeSet ? ` for change set ${verification.changeSet}` : ''}`];
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'th-run-'));
  // The tenant's configuration set travels in the package (R-09-02); an environment may override it.
  const environment: DeployEnvironment = { ...req.environment, tenant: req.environment.tenant ?? manifest.tenant };
  const packaged = environment.tenant ? path.join(dir, 'configuration', `${environment.tenant}.yaml`) : undefined;
  if (!environment.configurationSet && packaged && fs.existsSync(packaged)) environment.configurationSet = parseYaml(fs.readFileSync(packaged, 'utf8'));
  if (environment.configurationSet) log.push(`configuration ${environment.tenant}@${environment.configurationSet.release}${req.environment.configurationSet ? '' : ' from the package'}`);
  if (process.env.TH_RUNTIME_URL) log.push(`values from the runtime at ${process.env.TH_RUNTIME_URL}, in force at each attempt's time`);

  // 1. Workers: one process per queue, each on a free port; or an installation's workers, shared by distributions (R-09-133).
  let set: WorkerSet | undefined;
  try {
    if (req.workers) log.push(`workers: the installation's ${req.workers.procs.length}, shared by distributions; each attempt names ${req.distribution ?? 'no distribution'}`);
    set = req.workers ? undefined : await startWorkers({ packageDir: dir, environment, studioRoot: req.studioRoot, basePort: req.basePort, distribution: req.distribution, log, workDir: tmp });
    const procs = (set ?? req.workers!).procs;
    // On shared workers a payment's own ids keep its conversation and its inbound message apart from another payment's.
    const own = req.workers && req.paymentId ? req.paymentId : undefined;
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

    // 3. The front door: the scheme's intake map, on the worker of its queue. A load run (req.load) sends each of its
    // files through it, each with ids of its own; a deployment, its one inbound message.
    let ctx: any = req.context ?? {};
    let intake: DeployResult['intake'];
    let endToEnd = false;
    let legs: Record<string, string> | undefined;
    const start = Date.parse(`${req.environment.evaluationDate}T${req.environment.startTime ?? '06:00:00'}Z`);
    const frontDoor = (inbound: NonNullable<DeployRequest['inbound']>, n: number, base: any) => inSpan('th.payment.intake', attrs({ 'th.payment.id': `P-${n}` }), async () => {
      let ctx: any = base;
      let legs: Record<string, string> | undefined;
      const schemes = Object.values(documents).filter((d: any) => d.kind === 'scheme');
      const resolve = (n: string) => Object.entries(documents).find(([k]) => k.startsWith(`${n}@`))?.[1];
      const found = inbound.message
        ? (() => { for (const s of schemes) { const e = (s.messages ?? []).find((m: any) => m.id === inbound.message); if (e) return { scheme: s, entry: e }; } return undefined; })()
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
      const msg = { id: own && n === 1 ? `in-${own}` : `in-${n}`, type: entry.id, payload: inbound.payload };
      // End to end when the workflow is not the leg the message starts: the front door runs the
      // platform workflow that workflow-select picks, with that leg in its slot (R-05-08).
      const endToEnd = entry.disposition.start.leg !== req.workflow;
      const direction = endToEnd ? (entry.direction === 'in' ? 'inward' : 'outward') : wf.direction ?? 'outward';
      ctx = { header: { conversation: own && n === 1 ? `conv-${own}` : `conv-${n}`, paymentId: n === 1 && req.paymentId ? req.paymentId : `P-${n}`, scheme: scheme.scope, direction, receivedAt: new Date(start).toISOString(), ...(endToEnd ? routeOf(entry.disposition.start) : { kind: entry.disposition.start.kind }) }, messages: { inbound: { id: msg.id, type: msg.type } }, ...ctx };
      const r = await (await fetch(`${urlOf(queue)}/attempt`, { method: 'POST', headers: traceHeaders(), body: JSON.stringify({ instance: n === 1 ? 'intake' : `intake-${n}`, activity: map, step: 'intake', call: 1, attempt: 1, now: start, ctx, messages: [msg], ...(req.distribution ? { distribution: req.distribution } : {}) }) })).json();
      if (r.kind !== 'result') throw new Error(`Intake ${map} failed: ${r.message ?? r.kind}`);
      for (const [p, v] of Object.entries<any>(r.writes ?? {})) setPath(ctx, p, v);
      const intake: DeployResult['intake'] = { activity: map, queue, outcome: r.outcome };
      if (n === 1) log.push(`intake ${map} on ${queue}: ${r.outcome}`);
      if (r.outcome !== 'mapped') throw new Error(`Intake ${map} returned ${r.outcome}`);
      // The dispatch table routes, not the intake map (R-05-42): its values stand after the map.
      if (endToEnd) Object.assign(ctx.header, routeOf(entry.disposition.start), { scheme: scheme.scope });
      if (endToEnd) {
        // The same route the runtime's front door works out (route.ts, R-09-85).
        const byName = (nm: string) => Object.values<any>(documents).find((d) => d.name === nm);
        const chosen = selectWorkflow(byName, ctx.header, req.environment.evaluationDate);
        if (n === 1) log.push(`front door: workflow-select chose ${chosen ?? 'nothing'} for ${ctx.header.kind} ${ctx.header.direction} ${ctx.header.segment ?? ''}`.trimEnd());
        if (chosen !== req.workflow) throw new Error(`The front door chose ${chosen ?? 'no workflow'} for ${entry.id}, not ${req.workflow}`);
        legs = startRoute(schemes, byName, entry.id, req.environment.evaluationDate).legs;
        if (n === 1) log.push(`front door: ${entry.disposition.start.leg} fills ${Object.keys(legs).join(', ')}`);
      }
      // The payload stays with the workers by reference; every worker can resolve it.
      for (const pr of procs) if (pr.url !== urlOf(queue)) await fetch(`${pr.url}/attempt`, { method: 'POST', headers: traceHeaders(), body: JSON.stringify({ instance: n === 1 ? 'intake' : `intake-${n}`, activity: '__register__', step: 'intake', call: 1, attempt: 1, now: start, ctx, messages: [msg] }) });
      return { ctx, legs, intake, endToEnd };
    });
    if (req.inbound && !req.load) ({ ctx, legs, intake, endToEnd } = await frontDoor(req.inbound, 1, ctx));
    const runs: { label: string; context: any; legs?: Record<string, string>; intakeMs: number }[] = [];
    if (req.load) {
      if (engine !== 'temporal') throw new Error('The load harness runs on Temporal');
      for (const [i, f] of req.load.inbounds.entries()) {
        const t = Date.now();
        const r = await frontDoor({ message: req.inbound?.message, payload: f.payload }, i + 1, req.context ?? {});
        runs.push({ label: f.label, context: r.ctx, legs: r.legs, intakeMs: Date.now() - t });
        ({ ctx, legs, intake, endToEnd } = r);
      }
      log.push(`load: ${runs.length} file(s) through the front door, concurrency ${req.load.concurrency}`);
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
      // Items of a forEach that holds no state run side by side, as the binding says (R-09-45).
      itemConcurrency: binding.defaults?.forEachConcurrency ?? 1,
      // A bank's distribution runs as its own copy (R-09-55): the engine names its queues, orchestrations or deployment after it.
      ...(req.distribution ? { distribution: req.distribution } : {}),
      // On shared workers each payment's root instance is its own, as the long-running host names it.
      ...(own ? { rootId: `root-${own}` } : {}),
      built: { id: 'DEPLOY', spec: {}, workflow: req.workflow, ...(legs ? { legs } : {}), config: { evaluationDate: environment.evaluationDate, ...(environment.startTime ? { startTime: environment.startTime } : {}), tenant: environment.tenant, bindings: environment.configurationSet?.bindings ?? [], values: [...(environment.configurationSet?.values ?? []), ...(environment.values ?? [])] }, context: ctx },
      documents,
      workers: Object.fromEntries(procs.map((p) => [p.queue, p.url])),
      queues: binding.queues,
      // The BPMN the studio exported for the Flowable REST server, deployed as it is (R-09-15).
      ...(engine === 'flowable' ? { bpmn: bpmnOf(dir) } : {}),
      ...(req.load ? { runs: runs.map(({ label, context, legs }) => ({ label, context, legs })), concurrency: req.load.concurrency, worker: req.load.worker ?? {} } : {}),
    };
    const inFile = path.join(tmp, 'input.json');
    fs.writeFileSync(inFile, JSON.stringify(input));
    const t0 = Date.now();
    // Flowable with a store keeps its binding's state as facts under a run key (Issue I-89, R-09-65): a binding that stops is
    // started again with the same key and goes on from them, as a host's supervisor restarts it (at most three times here).
    const journalled = engine === 'flowable' && onStore && !req.load;
    const runKey = process.env.TH_FLOWABLE_RUN ?? (journalled ? `deploy-${process.pid}-${Date.now().toString(36)}` : undefined);
    const once = () => new Promise<{ stdout: string; stderr: string; code: number | null; signal: NodeJS.Signals | null }>((resolve) => {
      const child = spawn('dotnet', [harnessDll, ...(req.load ? ['--load'] : []), inFile], { stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, ...childEnv(), ...(runKey ? { TH_FLOWABLE_RUN: runKey } : {}) } });
      let stdout = '';
      let stderr = '';
      child.stdout.on('data', (d) => (stdout += d));
      child.stderr.on('data', (d) => (stderr += d));
      child.on('close', (code, signal) => resolve({ stdout, stderr, code, signal }));
    });
    let r = await once();
    for (let restart = 1; journalled && restart <= 3 && !r.stdout.includes('OBSERVATION ') && !r.stdout.includes('ERROR ') && (r.code === 75 || r.signal); restart++) {
      log.push(`engine: the Flowable binding stopped (${r.signal ?? `exit ${r.code}`}); started again with run key ${runKey} (restart ${restart})`);
      const before = r.stderr;
      r = await once();
      r.stderr = `${before}\n${r.stderr}`;
    }
    const lines = r.stdout.split('\n');
    if (req.load) {
      const loadLine = lines.find((l) => l.startsWith('LOAD '));
      if (!loadLine) {
        const err = lines.find((l) => l.startsWith('ERROR '));
        throw new Error(err ? JSON.parse(err.slice(6)) : `The engine exited ${r.code}: ${r.stderr.slice(-2000)}`);
      }
      const measured = JSON.parse(loadLine.slice('LOAD '.length));
      for (const [i, x] of (measured.runs ?? []).entries()) if (x) x.intakeMs = runs[i]?.intakeMs;
      const workers: DeployResult['workers'] = [];
      for (const pr of procs) {
        const st = await (await fetch(`${pr.url}/stats`)).json();
        workers.push({ queue: pr.queue, port: pr.port, pid: pr.child?.pid ?? 0, tasks: pr.tasks, served: st.served, refused: st.refused });
      }
      return { workflow: req.workflow, release: wf.release, trace: [], detail: [], attempts: {}, workers, messages: [], engine: { log: r.stderr.split('\n').slice(-200000) }, elapsedMs: Date.now() - t0, log, verification, load: measured };
    }
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
      workers.push({ queue: pr.queue, port: pr.port, pid: pr.child?.pid ?? 0, tasks: pr.tasks, served: st.served, refused: st.refused, valuesRead: st.valuesRead ?? 0 });
    }
    return {
      workflow: req.workflow, release: wf.release, expandedFrom: wf.expandedFrom,
      outcome: (root.trace ?? []).slice(-1)[0]?.replace(/^end:/, ''), failed: root.failed,
      trace: root.trace ?? [], detail: root.detail ?? [], attempts: root.attempts ?? {},
      intake, workers, messages: obs.messages ?? [], engine: obs.engine, elapsedMs: Date.now() - t0, log, verification,
    };
  } finally {
    // Stop the workers this run started and wait for each to exit, so that none outlives the deploy.
    await set?.stop();
    if (!req.keep) {
      fs.rmSync(tmp, { recursive: true, force: true });
      // The folder a zip was unpacked to is the runner's own; a folder the caller gave is not.
      if (dir !== req.packagePath) fs.rmSync(dir, { recursive: true, force: true });
    }
  }
}

/**
 * Starts a package's workers, one process per task queue, each on a free port (port 0: the system picks it, the worker says
 * which). A worker that was asked to restart (POST /restart, R-09-63) is started again from the same package.
 */
export async function startWorkers(o: StartWorkers): Promise<WorkerSet> {
  const manifest = parseYaml(fs.readFileSync(path.join(o.packageDir, 'manifest.yaml'), 'utf8'));
  const own = !o.workDir;
  const work = o.workDir ?? fs.mkdtempSync(path.join(os.tmpdir(), 'th-workers-'));
  const log = o.log ?? [];
  const envFile = path.join(work, 'environment.json');
  fs.writeFileSync(envFile, JSON.stringify(o.environment));
  const procs: WorkerProc[] = [];
  let port = o.basePort;
  let stopping = false;
  const startWorker = (w: any, p: number) => {
    const wdir = path.join(o.packageDir, w.path);
    const child = spawn(process.execPath, [path.join(wdir, 'worker.cjs'), '--dir', wdir, '--port', String(p), '--env', envFile], {
      env: { PATH: process.env.PATH, ...childEnv(), TH_PY_HARNESS: path.join(wdir, 'py_harness.py'), TH_SCRIPT_WORKER: path.join(wdir, 'script-worker.cjs'), TH_SIM_SHARED_DIR: path.join(work, 'simulators'), NODE_PATH: path.join(o.studioRoot, 'node_modules'), ...(process.env.TH_WORKER_LOG ? { TH_WORKER_LOG: process.env.TH_WORKER_LOG } : {}), ...(process.env.TH_NATIVE_HOST ? { TH_NATIVE_HOST: process.env.TH_NATIVE_HOST } : {}), ...(o.distribution ? { TH_DISTRIBUTION: o.distribution } : {}), ...(process.env.TH_NATIVE_ACTIVITIES ? { TH_NATIVE_ACTIVITIES: process.env.TH_NATIVE_ACTIVITIES } : {}), ...(process.env.TH_RUNTIME_URL ? { TH_RUNTIME_URL: process.env.TH_RUNTIME_URL } : {}), ...runtimeTokenEnv() },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    child.stderr?.on('data', (d) => log.push(`[${w.queue}] ${String(d).trim()}`));
    return child;
  };
  // A worker that was asked to restart drains and exits with code 75: it is started again from the same package, so at the same
  // version, on the same port; the engine's router waits for it meanwhile.
  const superviseRestart = (pr: WorkerProc, w: any) => {
    pr.child!.once('exit', (code) => {
      if (stopping || code !== 75) return;
      pr.restarts++;
      pr.child = startWorker(w, pr.port);
      log.push(`worker ${pr.queue} restarted on ${pr.port}, pid ${pr.child.pid}`);
      superviseRestart(pr, w);
    });
  };
  const stop = async () => {
    stopping = true;
    await Promise.all(procs.map((p) => new Promise<void>((done) => {
      if (!p.child || p.child.exitCode !== null || p.child.signalCode !== null) return done();
      p.child.once('exit', () => done());
      p.child.kill();
    })));
    if (own) fs.rmSync(work, { recursive: true, force: true });
  };
  try {
    for (const w of manifest.workers ?? []) {
      const p = port === undefined ? 0 : port++;
      procs.push({ queue: w.queue, port: p, child: startWorker(w, p), url: `http://127.0.0.1:${p}`, tasks: w.tasks, restarts: 0 });
    }
    for (const [i, pr] of procs.entries()) {
      if (pr.port === 0) {
        pr.port = await listening(pr.child!, pr.queue);
        pr.url = `http://127.0.0.1:${pr.port}`;
      }
      const h = await waitHealthy(pr.url);
      log.push(`worker ${h.queue} up on ${pr.port} with ${h.tasks.length} task(s), pid ${pr.child!.pid}${o.distribution ? ` for ${o.distribution}` : ''}`);
      superviseRestart(pr, manifest.workers[i]);
    }
  } catch (e) {
    await stop();
    throw e;
  }
  return { procs, stop };
}

/**
 * What a worker needs to call the runtime's values API (R-09-27): TH_RUNTIME_TOKEN, a reference to the token, and the
 * variable it names when it is an env: reference. The worker resolves it itself; the deploy runner never reads it.
 */
function runtimeTokenEnv(): Record<string, string> {
  // The worker's token for the runtime's values API, and for the native host (R-08-17): each a reference the worker resolves.
  // The native host's client credentials and client certificate are references too (R-08-21); the token endpoint, client id,
  // scope and audience are plain settings.
  const refs = (['TH_RUNTIME_TOKEN', 'TH_NATIVE_TOKEN', 'TH_NATIVE_CLIENT_SECRET', 'TH_NATIVE_CLIENT_CERT', 'TH_NATIVE_CLIENT_KEY', 'TH_NATIVE_CA'] as const).filter((k) => process.env[k] && isSecretRef(process.env[k]!));
  const plain = (['TH_NATIVE_TOKEN_URL', 'TH_NATIVE_CLIENT_ID', 'TH_NATIVE_SCOPE', 'TH_NATIVE_AUDIENCE'] as const).filter((k) => process.env[k]);
  if (!refs.length && !plain.length) return {};
  const out: Record<string, string> = Object.fromEntries([...refs, ...plain].map((k) => [k, process.env[k]!]));
  for (const k of ['TH_SECRET_FILE_ROOTS', 'TH_VAULT_ADDR', 'TH_VAULT_TOKEN', 'TH_VAULT_NAMESPACE']) if (process.env[k]) out[k] = process.env[k]!;
  for (const r of [...refs.map((k) => process.env[k]), process.env.TH_VAULT_TOKEN]) {
    const name = r?.startsWith('env:') ? r.slice(4) : undefined;
    if (name && process.env[name]) out[name] = process.env[name]!;
  }
  return out;
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
