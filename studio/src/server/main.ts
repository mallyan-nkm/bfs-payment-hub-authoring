import * as http from 'node:http';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { openEnv } from '../env.js';
import { connectionFromEnv, NOT_CONNECTED } from '../content/content-store.js';
import { dispatch } from './api.js';
import { attrs, contextFrom, inSpan, initTelemetry, meters, shutdownTelemetry, SpanKind } from '../telemetry/telemetry.js';

// The studio server: the API under /api and the built web app from web/dist. Its only store is the
// content branch the environment names (R-11-21): without TH_CONTENT_REPO it does not start.

if (!connectionFromEnv()) {
  console.error(`The studio does not start: TH_CONTENT_REPO is not set.\n${NOT_CONNECTED}`);
  process.exit(1);
}
let env: Awaited<ReturnType<typeof openEnv>>;
try {
  env = await openEnv();
} catch (e: any) {
  console.error(`The studio does not start: ${e?.message ?? e}`);
  process.exit(1);
}
// Traces, metrics and logs over OTLP when OTEL_* names an endpoint (R-10-07); a request is a span, its route a dimension.
initTelemetry('transacthub-studio');
for (const sig of ['SIGTERM', 'SIGINT'] as const) process.once(sig, () => { void shutdownTelemetry().finally(() => process.exit(0)); });
const port = Number(process.env.PORT ?? 4317);
const webRoot = path.join(env.studioRoot, 'web', 'dist');

const TYPES: Record<string, string> = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.json': 'application/json', '.ico': 'image/x-icon' };

/** The route of a request without its ids: /api/<first segment>, so a metric's dimensions stay few. */
function routeOf(p: string): string {
  const parts = p.split('/').filter(Boolean);
  return parts[0] === 'api' ? `/api/${parts[1] ?? ''}` : p.startsWith('/health') ? p : '/';
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url ?? '/', 'http://localhost');
  // Liveness and readiness (R-10-09): the process answers; it has read the content branch and is not mid-change.
  if (url.pathname === '/health/live') return void res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({ status: 'live' }));
  if (url.pathname === '/health/ready') {
    const c = env.store.content;
    const ready = !!c && !!c.head;
    return void res.writeHead(ready ? 200 : 503, { 'content-type': 'application/json' }).end(JSON.stringify({ status: ready ? 'ready' : 'not ready', content: ready ? c!.head.slice(0, 12) : null }));
  }
  const route = routeOf(url.pathname);
  const t0 = performance.now();
  res.on('finish', () => meters().httpServer.record((performance.now() - t0) / 1000, attrs({ 'http.request.method': req.method, 'http.route': route, 'http.response.status_code': res.statusCode })));
  void inSpan(`${req.method} ${route}`, attrs({ 'http.request.method': req.method, 'http.route': route, 'url.path': url.pathname }), async (span) => {
    await handle(req, res, url);
    span.setAttribute('http.response.status_code', res.statusCode);
  }, { kind: SpanKind.SERVER, parent: contextFrom(req.headers) }).catch(() => {
    if (!res.headersSent) res.writeHead(500, { 'content-type': 'application/json' }).end(JSON.stringify({ error: 'Internal error' }));
  });
});

async function handle(req: http.IncomingMessage, res: http.ServerResponse, url: URL) {
  if (url.pathname.startsWith('/api/')) {
    let raw = '';
    for await (const chunk of req) raw += chunk;
    let body: any = undefined;
    try {
      body = raw ? JSON.parse(raw) : undefined;
    } catch {
      res.writeHead(400, { 'content-type': 'application/json' }).end(JSON.stringify({ error: 'Body is not JSON' }));
      return;
    }
    // Before a change, read the branch again if someone else wrote to it since (R-11-21).
    if (req.method !== 'GET' && env.store.content && !env.store.content.busy) {
      try {
        if (await env.store.content.moved()) await env.store.reload();
      } catch {
        // GitHub cannot be reached: the change itself will say so.
      }
    }
    const out = await dispatch(env, { method: req.method ?? 'GET', path: url.pathname, query: Object.fromEntries(url.searchParams), body });
    if (out.contentType) {
      res.writeHead(out.status, { 'content-type': out.contentType, 'content-disposition': `attachment; filename="${out.filename}"` }).end(out.body);
      return;
    }
    res.writeHead(out.status, { 'content-type': 'application/json' }).end(JSON.stringify(out.body));
    return;
  }
  // Static web app, with the single-page fallback.
  let file = path.join(webRoot, url.pathname === '/' ? 'index.html' : url.pathname);
  if (!file.startsWith(webRoot) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) file = path.join(webRoot, 'index.html');
  if (!fs.existsSync(file)) {
    res.writeHead(200, { 'content-type': 'text/plain' }).end('The web app is not built. Run: npm run web:build');
    return;
  }
  res.writeHead(200, { 'content-type': TYPES[path.extname(file)] ?? 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
}

server.listen(port, () => {
  console.log(`TransactHub Designer Studio on http://localhost:${port}`);
  const c = env.store.content!;
  console.log(`Content: ${c.repository} ${c.conn.branch} (${c.conn.root}/) at ${c.head.slice(0, 12)}, read into memory. ${env.store.all().length} documents, ${c.drafts.size} change set branch(es) in progress.`);
});
