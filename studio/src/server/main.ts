import * as http from 'node:http';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { openEnv } from '../env.js';
import { dispatch } from './api.js';

// The studio server: the API under /api and the built web app from web/dist.

const env = openEnv();
const port = Number(process.env.PORT ?? 4317);
const webRoot = path.join(env.studioRoot, 'web', 'dist');

const TYPES: Record<string, string> = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.json': 'application/json', '.ico': 'image/x-icon' };

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url ?? '/', 'http://localhost');
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
});

server.listen(port, () => {
  console.log(`TransactHub Designer Studio on http://localhost:${port}`);
  console.log(`Spec: ${env.specRoot}. ${env.store.all().length} documents loaded.`);
});
