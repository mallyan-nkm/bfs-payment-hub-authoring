import { spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as http from 'node:http';
import * as https from 'node:https';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { GitHubClient, proxyDispatcher } from '../src/content/github.js';

// The GitHub client honours the environment's proxy settings (HTTPS_PROXY, HTTP_PROXY, NO_PROXY) and the
// certificate authorities in NODE_EXTRA_CA_CERTS, as a bank's network needs. A local proxy records what goes
// through it; a local API answers one ref, over HTTPS with a certificate of a test-only authority, and over HTTP.

const STUDIO = path.resolve(__dirname, '..');
const PROXY_VARS = ['HTTPS_PROXY', 'https_proxy', 'HTTP_PROXY', 'http_proxy', 'NO_PROXY', 'no_proxy', 'NODE_EXTRA_CA_CERTS', 'NODE_USE_ENV_PROXY'];
const SHA = '0123456789abcdef0123456789abcdef01234567';
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-gh-proxy-'));
const hasOpenssl = spawnSync('openssl', ['version']).status === 0;
const seen: string[] = [];
let proxy: http.Server;
let api: http.Server;
let tlsApi: https.Server | undefined;
let proxyUrl = '';
let apiUrl = '';
let tlsUrl = '';

function answer(req: http.IncomingMessage, res: http.ServerResponse) {
  if (req.url === '/repos/o/r/git/ref/heads/main') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ ref: 'refs/heads/main', object: { sha: SHA, type: 'commit' } }));
  } else {
    res.writeHead(404).end('{}');
  }
}

const listen = (s: net.Server) => new Promise<number>((r) => s.listen(0, '127.0.0.1', () => r((s.address() as net.AddressInfo).port)));

beforeAll(async () => {
  // A forwarding proxy: CONNECT tunnels for https, absolute-URL requests for http.
  proxy = http.createServer((req, res) => {
    seen.push(`${req.method} ${req.url}`);
    const u = new URL(req.url!);
    const up = http.request({ host: u.hostname, port: u.port, path: u.pathname + u.search, method: req.method, headers: req.headers }, (r) => {
      res.writeHead(r.statusCode ?? 502, r.headers);
      r.pipe(res);
    });
    req.pipe(up);
  });
  proxy.on('connect', (req, socket, head) => {
    seen.push(`CONNECT ${req.url}`);
    const [host, port] = req.url!.split(':');
    const up = net.connect(Number(port), host, () => {
      socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
      up.write(head);
      up.pipe(socket);
      socket.pipe(up);
    });
    up.on('error', () => socket.destroy());
    socket.on('error', () => up.destroy());
  });
  proxyUrl = `http://127.0.0.1:${await listen(proxy)}`;
  api = http.createServer(answer);
  apiUrl = `http://127.0.0.1:${await listen(api)}`;
  if (hasOpenssl) {
    // A test-only authority and a certificate for 127.0.0.1 it signed; nothing outside this test trusts it.
    const o = (...a: string[]) => {
      const r = spawnSync('openssl', a, { cwd: work, encoding: 'utf8' });
      if (r.status !== 0) throw new Error(r.stderr);
    };
    o('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', 'ca.key', '-out', 'ca.pem', '-days', '2', '-subj', '/CN=TransactHub test-only CA');
    o('req', '-newkey', 'rsa:2048', '-nodes', '-keyout', 'api.key', '-out', 'api.csr', '-subj', '/CN=127.0.0.1');
    fs.writeFileSync(path.join(work, 'san.ext'), 'subjectAltName=IP:127.0.0.1\n');
    o('x509', '-req', '-in', 'api.csr', '-CA', 'ca.pem', '-CAkey', 'ca.key', '-CAcreateserial', '-out', 'api.pem', '-days', '2', '-extfile', 'san.ext');
    tlsApi = https.createServer({ key: fs.readFileSync(path.join(work, 'api.key')), cert: fs.readFileSync(path.join(work, 'api.pem')) }, answer);
    tlsUrl = `https://127.0.0.1:${await listen(tlsApi)}`;
  }
});

afterAll(() => {
  proxy?.close();
  api?.close();
  tlsApi?.close();
  fs.rmSync(work, { recursive: true, force: true });
});

/** The client in a process of its own, with exactly these proxy and CA settings (not spawnSync: this process serves the proxy and the API). */
function child(apiBase: string, env: Record<string, string>) {
  const base = Object.fromEntries(Object.entries(process.env).filter(([k]) => !PROXY_VARS.includes(k)));
  return new Promise<string>((resolve) => {
    let out = '';
    const c = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), [path.join(STUDIO, 'test', 'support', 'github-proxy-child.ts'), apiBase], { cwd: STUDIO, env: { ...base, ...env } });
    const timer = setTimeout(() => c.kill('SIGKILL'), 60000);
    c.stdout.on('data', (d) => (out += d));
    c.stderr.on('data', (d) => (out += d));
    c.on('close', () => { clearTimeout(timer); resolve(out.trim()); });
  });
}

describe('the GitHub client behind a proxy', () => {
  it.skipIf(!hasOpenssl)('goes through HTTPS_PROXY, and trusts the authority NODE_EXTRA_CA_CERTS names', async () => {
    seen.length = 0;
    const out = await child(tlsUrl, { HTTPS_PROXY: proxyUrl, NODE_EXTRA_CA_CERTS: path.join(work, 'ca.pem') });
    expect(out).toBe(`SHA ${SHA}`);
    expect(seen).toEqual([`CONNECT ${tlsUrl.replace('https://', '')}`]);
  });

  it.skipIf(!hasOpenssl)('refuses a certificate whose authority it was not given', async () => {
    seen.length = 0;
    const out = await child(tlsUrl, { HTTPS_PROXY: proxyUrl });
    expect(out).toMatch(/^ERR GitHub cannot be reached at https:\/\/127\.0\.0\.1:\d+: .*(certificate|self.signed|issuer)/i);
  });

  it.skipIf(!hasOpenssl)('goes straight to a host NO_PROXY names', async () => {
    seen.length = 0;
    const out = await child(tlsUrl, { HTTPS_PROXY: proxyUrl, NO_PROXY: 'localhost,127.0.0.1', NODE_EXTRA_CA_CERTS: path.join(work, 'ca.pem') });
    expect(out).toBe(`SHA ${SHA}`);
    expect(seen).toEqual([]);
  });

  it('goes through HTTP_PROXY to an http: API base, and reads the settings again when they change', async () => {
    const saved = Object.fromEntries(PROXY_VARS.map((k) => [k, process.env[k]]));
    try {
      for (const k of PROXY_VARS) delete process.env[k];
      process.env.HTTP_PROXY = proxyUrl;
      seen.length = 0;
      const c = new GitHubClient({ api: apiUrl, owner: 'o', repo: 'r', token: () => 'synthetic-test-token' });
      const first = proxyDispatcher();
      expect(await c.getRef('main')).toBe(SHA);
      // undici's proxy agent tunnels an http: request as well (CONNECT), as curl does with --proxytunnel.
      expect(seen).toEqual([`CONNECT ${apiUrl.replace('http://', '')}`]);
      process.env.NO_PROXY = '127.0.0.1';
      expect(proxyDispatcher()).not.toBe(first);
      expect(await c.getRef('main')).toBe(SHA);
      expect(seen).toHaveLength(1);
    } finally {
      for (const [k, v] of Object.entries(saved)) {
        if (v === undefined) delete process.env[k];
        else process.env[k] = v;
      }
      proxyDispatcher();
    }
  });
});
