// The laptop's workers service (deploy/laptop, R-09-105): every activity worker of every distribution of the runtime's bank, side by
// side, behind one address on the compose network. `workers` in the runtime image.
//
//   http://workers:7700/<distribution>/<workflow>/<queue>/<path>     e.g. /dist%2Fadib%402026.11.23-1/p.wf.corporate.v1/th-uae-wps/attempt
//
// The engine host's plans name it by template (TH_HOST_WORKERS_URL), and the File API's front door for the intake maps
// (FileApi__Workers__<scope>), each with the distribution the payment is pinned to. On a distribution's first call this service
// fetches its package from the release API (with the front door's client credentials), verifies it against the trusted signers
// (R-09-26), and starts the workers of its engine packages, one process per queue, each from that distribution's own copy: a
// payment pinned to A is served by A's workers, never B's. An attempt goes to the worker whose tasks name its activity and release
// (<name>@<release>), preferring the workflow named; the workflow `_` and the queue `any` let the task alone choose.
//
// Each worker's environment is the distribution's own (its engine package's configuration for the bank, the values read from the
// bank's reference tables, R-09-103), the bank's values from its installed configuration release (TH_RUNTIME_URL), and, the laptop being a test environment, the simulators TH_WORKERS_BENCH names. Nothing here
// holds a secret: the client secret is read from the environment, by reference.
import { spawn, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as http from 'node:http';
import * as path from 'node:path';
import { parseYaml } from '../../../studio/src/core/yaml.js';
import { distributionCopy, type RuntimeClient } from '../../../studio/src/deploy/front-door.js';

const env = (k: string, d?: string) => process.env[k] ?? d ?? (() => { throw new Error(`Set ${k}`); })();
const bank = env('TH_BANK');
const home = env('TH_WORKERS_HOME', '/var/lib/transacthub/work/workers');
const port = Number(env('TH_WORKERS_PORT', '7700'));
const bench = JSON.parse(env('TH_WORKERS_BENCH', '[]'));
const studio = env('TH_STUDIO_ROOT', '/opt/th/studio');
fs.mkdirSync(home, { recursive: true });
const log = (s: string) => console.log(`${new Date().toISOString()} workers: ${s}`);

let token: { value: string; until: number } | undefined;
async function runtime(): Promise<RuntimeClient> {
  if (!token || token.until < Date.now()) {
    const r = await fetch(env('TH_IDP_TOKEN_URL'), {
      method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ grant_type: 'client_credentials', client_id: env('TH_WORKERS_CLIENT_ID', 'th-front-door'), client_secret: env('TH_LAPTOP_FRONTDOOR_SECRET') }),
    });
    if (!r.ok) throw new Error(`The bank's identity provider refused the workers: ${r.status}`);
    const j: any = await r.json();
    token = { value: j.access_token, until: Date.now() + Math.max(60, (j.expires_in ?? 600) - 120) * 1000 };
  }
  return { url: env('TH_RUNTIME_API'), headers: { authorization: `Bearer ${token.value}` } };
}

const pass = (...keys: string[]) => Object.fromEntries(keys.filter((k) => process.env[k]).map((k) => [k, process.env[k]!]));

interface Worker { distribution: string; workflow: string; queue: string; dir: string; tasks: string[]; url?: string; starting?: Promise<string>; child?: ChildProcess }
const distributions = new Map<string, Promise<Worker[]>>();

/** A distribution's workers, known once its package is fetched and verified; none is started until it is called. */
function workersOf(distribution: string): Promise<Worker[]> {
  let p = distributions.get(distribution);
  if (!p) {
    p = (async () => {
      if (!distribution.startsWith(`dist/${bank}@`)) throw new Error(`This runtime serves ${bank} only, not ${distribution} (R-09-76)`);
      const { dir } = await distributionCopy(await runtime(), distribution, path.join(home, 'distributions'));
      const out: Worker[] = [];
      const engine = path.join(dir, 'engine');
      for (const wf of fs.existsSync(engine) ? fs.readdirSync(engine).sort() : []) {
        const m = path.join(engine, wf, 'manifest.yaml');
        if (!fs.existsSync(m)) continue;
        for (const w of parseYaml(fs.readFileSync(m, 'utf8')).workers ?? []) out.push({ distribution, workflow: wf, queue: w.queue, dir: path.join(engine, wf, w.path), tasks: w.tasks ?? [] });
      }
      log(`${distribution}: ${out.length} worker(s) in ${new Set(out.map((w) => w.workflow)).size} engine package(s), verified`);
      return out;
    })();
    p.catch(() => distributions.delete(distribution));
    distributions.set(distribution, p);
  }
  return p;
}

/** The distribution's environment for a worker of one engine package: its configuration for the bank, and the laptop's simulators. */
function environmentFile(w: Worker): string {
  const pkg = path.dirname(path.dirname(w.dir));
  const file = path.join(pkg, `environment-${bank}.json`);
  if (!fs.existsSync(file)) {
    const cfg = path.join(pkg, 'configuration', `${bank}.yaml`);
    const environment = { tenant: bank, evaluationDate: new Date().toISOString().slice(0, 10), bench, ...(fs.existsSync(cfg) ? { configurationSet: parseYaml(fs.readFileSync(cfg, 'utf8')) } : {}) };
    fs.writeFileSync(file, JSON.stringify(environment));
  }
  return file;
}

function start(w: Worker): Promise<string> {
  if (w.url) return Promise.resolve(w.url);
  w.starting ??= new Promise<string>((resolve, reject) => {
    const shared = path.join(home, 'simulators', w.distribution.replace(/[^A-Za-z0-9._-]+/g, '_'));
    fs.mkdirSync(shared, { recursive: true });
    const child = spawn(process.execPath, [path.join(w.dir, 'worker.cjs'), '--dir', w.dir, '--port', '0', '--env', environmentFile(w)], {
      // The bank's values from its installed configuration release, at each attempt's time (TH_RUNTIME_URL, R-12-28): the token by
      // reference, a file the tokens service keeps fresh.
      // The worker runs one distribution's copy, its tasks and scripts the distribution's own (Issue I-232). Each attempt names its
      // payment's distribution (R-09-130); the native host refuses one for another distribution than this TH_DISTRIBUTION (R-09-131).
      env: { PATH: process.env.PATH, HOME: process.env.HOME, TH_DISTRIBUTION: w.distribution, ...pass('TH_RUNTIME_URL', 'TH_RUNTIME_TOKEN', 'TH_SECRET_FILE_ROOTS', 'TH_NATIVE_HOST', 'TH_NATIVE_ACTIVITIES', 'TH_NATIVE_TOKEN', 'TH_FILE_API_URL', 'TH_FILE_API_TOKEN'), TH_PY_HARNESS: path.join(w.dir, 'py_harness.py'), TH_SCRIPT_WORKER: path.join(w.dir, 'script-worker.cjs'), TH_SIM_SHARED_DIR: shared, NODE_PATH: path.join(studio, 'node_modules') },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    w.child = child;
    let out = '';
    const timer = setTimeout(() => reject(new Error(`worker ${w.queue} of ${w.distribution} did not start`)), 30000);
    child.stdout!.on('data', (d) => {
      out += d;
      const m = /worker \S+ on (\d+):/.exec(out);
      if (m && !w.url) {
        clearTimeout(timer);
        w.url = `http://127.0.0.1:${m[1]}`;
        log(`${w.distribution} ${w.workflow} ${w.queue}: up on ${m[1]} with ${w.tasks.length} task(s)`);
        resolve(w.url);
      }
    });
    child.stderr!.on('data', (d) => process.stderr.write(`[${w.queue}@${w.distribution}] ${d}`));
    child.on('exit', (code) => {
      log(`${w.distribution} ${w.workflow} ${w.queue}: exited ${code}`);
      w.url = undefined;
      w.starting = undefined;
    });
  });
  return w.starting;
}

/** The worker of a call: by the attempt's task, else by the workflow and queue named. */
function choose(all: Worker[], workflow: string, queue: string, body: any): Worker | undefined {
  const byQueue = all.filter((w) => queue === 'any' || w.queue === queue);
  const preferred = [...byQueue.filter((w) => w.workflow === workflow), ...byQueue.filter((w) => w.workflow !== workflow)];
  const activity = body?.activity;
  if (activity && activity !== '__register__') {
    const named = preferred.find((w) => w.tasks.some((t) => (body.release ? t === `${activity}@${body.release}` : t.startsWith(`${activity}@`))));
    if (named) return named;
  }
  return preferred[0];
}

const server = http.createServer(async (req, res) => {
  const send = (code: number, body: any) => { res.writeHead(code, { 'content-type': 'application/json' }); res.end(JSON.stringify(body)); };
  try {
    const url = new URL(req.url ?? '/', 'http://workers');
    if (url.pathname === '/health' || url.pathname === '/health/live') return send(200, { status: 'live', distributions: [...distributions.keys()] });
    const [d, workflow, queue, ...rest] = url.pathname.split('/').filter(Boolean).map(decodeURIComponent);
    if (!d || !workflow || !queue) return send(404, { error: 'Call /<distribution>/<workflow>/<queue>/<path>' });
    let raw = '';
    for await (const c of req) raw += c;
    let body: any;
    try { body = raw ? JSON.parse(raw) : undefined; } catch { body = undefined; }
    const w = choose(await workersOf(d), workflow, queue, body);
    if (!w) return send(200, { kind: 'error', name: 'NotOnThisQueue', retryable: false, message: `${d} has no worker of ${queue}`, deliveries: [] });
    const target = await start(w);
    const r = await fetch(`${target}/${rest.join('/')}${url.search}`, { method: req.method, headers: raw ? { 'content-type': 'application/json' } : {}, body: raw || undefined });
    res.writeHead(r.status, { 'content-type': r.headers.get('content-type') ?? 'application/json' });
    res.end(Buffer.from(await r.arrayBuffer()));
  } catch (e: any) {
    send(503, { kind: 'error', name: 'WorkersUnavailable', retryable: true, message: String(e?.message ?? e), deliveries: [] });
  }
});
for (const s of ['SIGTERM', 'SIGINT'] as const) process.once(s, () => {
  for (const p of distributions.values()) p.then((ws) => ws.forEach((w) => w.child?.kill()), () => {});
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 3000).unref();
});
server.listen(port, '0.0.0.0', () => log(`listening on ${port} for ${bank}`));
