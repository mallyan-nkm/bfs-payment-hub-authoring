import * as fs from 'node:fs';
import * as http from 'node:http';
import * as path from 'node:path';
import { Store } from '../core/store.js';
import { parseYaml } from '../core/yaml.js';
import { ScriptRegistry } from '../interp/sandbox.js';
import { PartyService, type AttemptRequest } from '../conformance/party.js';

// A deployed activity worker (Part 9; R-09-04, R-09-05). One process per task queue. It serves only
// the tasks its manifest lists, registered as <name>@<release>, and refuses any other. It holds
// the artefacts those tasks need (contracts, rule sets, rules, tables, ports, extensions) and their
// precompiled scripts; it runs them on the engine's clock, which each request carries. In a test
// deployment its environment binds simulators to the ports; in production the ports are the
// bank's own connections and no simulator is present (R-07-12).
//
//   node worker.cjs --port 7101 [--env environment.json]
//   GET  /health      -> { queue, tasks }
//   POST /attempt     -> an attempt of one task (AttemptRequest), with any later deliveries
//   POST /start       -> deliveries from inward simulators
//   GET  /sent        -> messages sent on ports
//   GET  /stats       -> tasks served and refused

const args = process.argv.slice(2);
const arg = (k: string, d?: string) => {
  const i = args.indexOf(`--${k}`);
  return i >= 0 ? args[i + 1] : d;
};
const dir = path.resolve(arg('dir', path.dirname(process.argv[1]))!);
const port = Number(arg('port', '7100'));
const manifest = JSON.parse(fs.readFileSync(path.join(dir, 'manifest.json'), 'utf8'));
const environment = arg('env') ? JSON.parse(fs.readFileSync(path.resolve(arg('env')!), 'utf8')) : {};
const tasks = new Set<string>(manifest.tasks);

const dirs = [path.join(dir, 'artefacts'), path.join(dir, 'simulators')].filter((d) => fs.existsSync(d));
const store = Store.fromDirs(dirs);
if (environment.configurationSet) {
  const cs = typeof environment.configurationSet === 'string' ? parseYaml(environment.configurationSet) : environment.configurationSet;
  store.add(cs, 'environment', 'spec', false, 'published', cs.tenant);
}
const party = new PartyService({
  store,
  scripts: new ScriptRegistry([path.join(dir, 'scripts')]),
  evaluationDate: environment.evaluationDate ?? new Date().toISOString().slice(0, 10),
  tenant: environment.tenant,
  bench: (environment.bench ?? []).filter((b: any) => b.simulator === 'runtime' || store.doc(b.simulator)),
  values: environment.values,
} as any);

const served: Record<string, number> = {};
let refused = 0;

function send(res: http.ServerResponse, code: number, body: any) {
  res.writeHead(code, { 'content-type': 'application/json' });
  res.end(JSON.stringify(body));
}

const server = http.createServer(async (req, res) => {
  let raw = '';
  for await (const c of req) raw += c;
  try {
    const url = new URL(req.url ?? '/', 'http://worker');
    if (req.method === 'GET' && url.pathname === '/health') return send(res, 200, { queue: manifest.queue, tasks: manifest.tasks, simulators: dirs.length > 1 });
    if (req.method === 'GET' && url.pathname === '/stats') return send(res, 200, { queue: manifest.queue, served, refused });
    if (req.method === 'GET' && url.pathname === '/sent') return send(res, 200, { messages: party.sent() });
    if (req.method === 'POST' && url.pathname === '/start') {
      const b = JSON.parse(raw || '{}');
      return send(res, 200, { deliveries: party.start(b.now ?? Date.now(), b.conversation) });
    }
    if (req.method === 'POST' && url.pathname === '/attempt') {
      const b = JSON.parse(raw) as AttemptRequest;
      if (b.activity === '__register__') {
        await party.attempt({ ...b, activity: '__none__' }).catch(() => undefined);
        return send(res, 200, { kind: 'registered' });
      }
      const release = b.release ?? store.latest(b.activity)?.release;
      const task = `${b.activity}@${release}`;
      if (!tasks.has(task)) {
        refused++;
        return send(res, 200, { kind: 'error', name: 'NotOnThisQueue', retryable: false, message: `${task} is not registered on queue ${manifest.queue}`, deliveries: [] });
      }
      served[task] = (served[task] ?? 0) + 1;
      const r = await party.attempt({ ...b, release });
      if (process.env.TH_WORKER_LOG) fs.appendFileSync(process.env.TH_WORKER_LOG, JSON.stringify({ queue: manifest.queue, task, step: b.step, messages: b.ctx?.messages, result: r }) + '\n');
      return send(res, 200, { ...r, deliveries: party.takeDeliveries() });
    }
    send(res, 404, { error: 'not found' });
  } catch (e: any) {
    send(res, 500, { error: String(e?.message ?? e) });
  }
});

server.listen(port, '127.0.0.1', () => console.log(`worker ${manifest.queue} on ${port}: ${manifest.tasks.length} task(s)`));
