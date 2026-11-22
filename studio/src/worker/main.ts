import * as fs from 'node:fs';
import * as http from 'node:http';
import * as path from 'node:path';
import { Store } from '../core/store.js';
import { parseYaml } from '../core/yaml.js';
import { ScriptRegistry } from '../interp/sandbox.js';
import { PartyService, type AttemptRequest } from '../conformance/party.js';
import { attrs, contextFrom, inSpan, initTelemetry, log, meters, shutdownTelemetry, SpanKind } from '../telemetry/telemetry.js';

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
//   GET  /health/live, /health/ready -> the process answers; it has loaded its tasks (R-10-09)
//
// Telemetry (R-10-07): with OTEL_EXPORTER_OTLP_ENDPOINT set, each attempt is a span in the trace the engine sent
// (traceparent), with the port calls it makes inside it, and the worker's metrics and logs go there too.

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
initTelemetry(process.env.OTEL_SERVICE_NAME ?? `transacthub-worker-${manifest.queue}`);
// Stopped by the deploy runner or the host: send what is buffered first.
for (const sig of ['SIGTERM', 'SIGINT'] as const) process.once(sig, () => { void shutdownTelemetry().finally(() => process.exit(0)); });

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
let valuesRead = 0;

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
    if (req.method === 'GET' && url.pathname === '/health/live') return send(res, 200, { status: 'live' });
    if (req.method === 'GET' && url.pathname === '/health/ready') return send(res, tasks.size > 0 ? 200 : 503, { status: tasks.size > 0 ? 'ready' : 'not ready', queue: manifest.queue, tasks: tasks.size });
    if (req.method === 'GET' && url.pathname === '/stats') return send(res, 200, { queue: manifest.queue, served, refused, valuesRead });
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
      // On a runtime (plan P6), the bank's values are the runtime's, in force at the engine's time of this attempt: a value
      // published with a later validFrom reaches the attempts of that date with no new deployment.
      const runtime = process.env.TH_RUNTIME_URL;
      if (runtime && environment.tenant) {
        try {
          const at = new Date(b.now ?? Date.now()).toISOString();
          const r = await fetch(`${runtime.replace(/\/$/, '')}/values?tenant=${encodeURIComponent(environment.tenant)}&at=${encodeURIComponent(at)}`);
          if (!r.ok) throw new Error(`HTTP ${r.status}`);
          (party.run.cfg as any).liveValues = Object.fromEntries(((await r.json()) as any[]).map((v) => [v.parameter, v.value]));
          valuesRead++;
        } catch (e: any) {
          return send(res, 200, { kind: 'error', name: 'ValuesUnavailable', retryable: true, message: `The runtime's values could not be read: ${e?.message ?? e}`, deliveries: [] });
        }
      }
      served[task] = (served[task] ?? 0) + 1;
      const t0 = performance.now();
      const a = attrs({ 'th.activity': b.activity, 'th.release': release, 'th.queue': manifest.queue });
      const { result: r, deliveries } = await inSpan(`th.activity ${b.activity}`, attrs({
        ...a, 'th.step': b.step, 'th.attempt': b.attempt, 'th.instance': b.instance, 'th.level': b.level, 'th.payment.id': b.ctx?.header?.paymentId,
      }), async (span) => {
        const out = await party.attemptOwn({ ...b, release });
        span.setAttributes(attrs({ 'th.kind': out.result.kind, 'th.outcome': (out.result as any).outcome, 'th.error': (out.result as any).name }));
        if (out.result.kind === 'error') span.setStatus({ code: 2, message: out.result.name });
        return out;
      }, { kind: SpanKind.SERVER, parent: contextFrom(req.headers) });
      meters().workerAttempt.record((performance.now() - t0) / 1000, { ...a, 'th.kind': r.kind });
      if (r.kind === 'error') {
        meters().workerErrors.add(1, { ...a, 'th.error': r.name });
        log('warn', 'Attempt of {th.activity} on {th.queue} ended in {th.error}', { 'th.activity': b.activity, 'th.queue': manifest.queue, 'th.error': r.name });
      }
      if (process.env.TH_WORKER_LOG) fs.appendFileSync(process.env.TH_WORKER_LOG, JSON.stringify({ queue: manifest.queue, task, step: b.step, messages: b.ctx?.messages, result: r }) + '\n');
      return send(res, 200, { ...r, deliveries });
    }
    send(res, 404, { error: 'not found' });
  } catch (e: any) {
    send(res, 500, { error: String(e?.message ?? e) });
  }
});

server.listen(port, '127.0.0.1', () => console.log(`worker ${manifest.queue} on ${port}: ${manifest.tasks.length} task(s)`));
