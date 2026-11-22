import type { AddressInfo } from 'node:net';
import { deflateRawSync } from 'node:zlib';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { DevProvider, MAX_PENDING_SIGN_INS, Sessions, authConfigFromEnv, parseCookies } from '../src/server/auth.js';
import { createStudioServer, isExpensive } from '../src/server/http.js';
import { RateLimiter, safeFilename } from '../src/server/hardening.js';
import { documentPath, scriptPath } from '../src/content/layout.js';
import { unzip, ZipRefused } from '../src/export/zip.js';
import { loadSample } from '../src/testing/runner.js';
import type { StudioEnv } from '../src/env.js';
import { dispatchAs } from './support/as-user.js';
import { freshEnv } from './helpers.js';

// The security review of November 2026 (docs/north-star/security/review-2026-11.md): one regression test per finding
// the studio's code fixed. The script sandboxes have theirs in sandbox.test.ts and python.test.ts.

let env: StudioEnv;
const servers: { close: () => void }[] = [];
beforeAll(() => {
  env = freshEnv();
});
afterAll(() => servers.forEach((s) => s.close()));

async function start(extra: Record<string, string> = {}) {
  const saved: Record<string, string | undefined> = {};
  for (const [k, v] of Object.entries(extra)) {
    saved[k] = process.env[k];
    process.env[k] = v;
  }
  try {
    const probe = createStudioServer(env, authConfigFromEnv({ TH_AUTH_DEV: '1', NODE_ENV: 'test' } as any, 1));
    await new Promise<void>((r) => probe.listen(0, '127.0.0.1', r));
    const port = (probe.address() as AddressInfo).port;
    probe.close();
    const url = `http://127.0.0.1:${port}`;
    const server = createStudioServer(env, authConfigFromEnv({ TH_AUTH_DEV: '1', NODE_ENV: 'test', TH_PUBLIC_URL: url } as any));
    await new Promise<void>((r) => server.listen(port, '127.0.0.1', r));
    servers.push(server);
    return url;
  } finally {
    for (const [k, v] of Object.entries(saved)) if (v === undefined) delete process.env[k];
      else process.env[k] = v;
  }
}

const as = (user: string, method: string, path: string, body: any = {}) => {
  const [p, q = ''] = path.split('?');
  return dispatchAs(env, { method, path: p, query: Object.fromEntries(new URLSearchParams(q)), body, user });
};

describe('the studio server answers with security headers (R-11-43)', () => {
  it('on the API, the sign-in and the web app, with a content security policy that allows no inline script and no frame', async () => {
    const url = await start();
    for (const p of ['/api/catalogue', '/auth/me', '/health/live', '/']) {
      const r = await fetch(url + p, { redirect: 'manual' });
      expect(r.headers.get('x-content-type-options'), p).toBe('nosniff');
      expect(r.headers.get('x-frame-options'), p).toBe('DENY');
      expect(r.headers.get('referrer-policy'), p).toBe('no-referrer');
      expect(r.headers.get('content-security-policy'), p).toMatch(/frame-ancestors 'none'/);
      expect(r.headers.get('content-security-policy'), p).not.toMatch(/unsafe-inline|unsafe-eval/);
    }
  });
});

describe('the studio server bounds what one caller can ask (R-11-41)', () => {
  it('refuses a body past the limit with 413, before it is read whole', async () => {
    const url = await start({ TH_MAX_BODY_MB: '1' });
    const big = 'x'.repeat(1024 * 1024 + 10);
    const r = await fetch(`${url}/api/drafts`, { method: 'POST', body: big, headers: { 'content-type': 'application/json' } }).catch((e) => e);
    // Unsigned, so refused already at the CSRF gate; a sign-in page post shows the body limit itself.
    expect(r.status === 401 || r.status === 413).toBe(true);
    const dev = await fetch(`${url}/auth/dev/authorize`, { method: 'POST', body: big, headers: { 'content-type': 'application/x-www-form-urlencoded' } });
    expect(dev.status).toBe(413);
  });
  it('limits sign-ins per client address with 429', async () => {
    const url = await start({ TH_RATE_AUTH_PER_MINUTE: '3' });
    const codes: number[] = [];
    for (let i = 0; i < 5; i++) codes.push((await fetch(`${url}/auth/login`, { redirect: 'manual' })).status);
    expect(codes).toEqual([302, 302, 302, 429, 429]);
  });
  it('holds no sign-in in progress in memory, so /auth/login cannot fill it (R-11-45)', () => {
    const s = new Sessions(authConfigFromEnv({ TH_AUTH_DEV: '1', NODE_ENV: 'test' } as any, 1));
    for (let i = 0; i < 1000; i++) s.begin('');
    expect(s.pendingCount).toBe(MAX_PENDING_SIGN_INS);
    expect(MAX_PENDING_SIGN_INS).toBe(0);
  });
  it('counts per key in a fixed minute, and names the heavy calls', () => {
    let t = 0;
    const l = new RateLimiter(2, () => t);
    expect([l.take('a'), l.take('a'), l.take('a'), l.take('b')]).toEqual([true, true, false, true]);
    t = 60_000;
    expect(l.take('a')).toBe(true);
    expect(isExpensive('POST', '/api/tests/run-all')).toBe(true);
    expect(isExpensive('POST', '/api/import/apply')).toBe(true);
    expect(isExpensive('POST', '/api/drafts')).toBe(false);
    expect(isExpensive('GET', '/api/catalogue')).toBe(false);
  });
});

describe('sign-in hardening (R-11-42)', () => {
  it('ends a session left unused past the idle time', () => {
    const cfg = { ...authConfigFromEnv({ TH_AUTH_DEV: '1', NODE_ENV: 'test' } as any, 1), idleMinutes: 30 };
    const s = new Sessions(cfg);
    const now = vi.spyOn(Date, 'now');
    try {
      now.mockReturnValue(1_000_000);
      const session = s.create('example.maker', 'example.maker');
      const cookie = s.sessionCookie(session).split(';')[0];
      now.mockReturnValue(1_000_000 + 29 * 60_000);
      expect(s.fromCookieHeader(cookie)?.id).toBe(session.id);
      now.mockReturnValue(1_000_000 + 29 * 60_000 + 31 * 60_000);
      expect(s.fromCookieHeader(cookie)).toBeUndefined();
    } finally {
      now.mockRestore();
    }
  });
  it('skips a cookie that does not decode instead of failing the request', async () => {
    expect(parseCookies('a=%E0%A4%A; th_session=x')).toEqual({ th_session: 'x' });
    const url = await start();
    expect((await fetch(`${url}/api/me`, { headers: { cookie: 'th_session=%E0%A4%A' } })).status).toBe(401);
  });
  it('the development provider sends a code only to the studio\'s own callback', () => {
    const dev = new DevProvider('http://127.0.0.1:4317/auth/dev', () => ['example.maker']);
    const form = (redirect: string) => new URLSearchParams({ client_id: 'studio-dev', redirect_uri: redirect, state: 's', nonce: 'n', code_challenge: 'c'.repeat(43), code_challenge_method: 'S256', response_type: 'code', user: 'example.maker' });
    const u = new URL('http://127.0.0.1:4317/auth/dev/authorize');
    expect(dev.handle('POST', u, form('https://elsewhere.example/steal'))!.status).toBe(400);
    expect(dev.handle('POST', u, form('http://127.0.0.1:4317/auth/other'))!.status).toBe(400);
    expect(dev.handle('POST', u, form('http://127.0.0.1:4317/auth/callback'))!.status).toBe(303);
  });
  it('wants an https identity provider in production', () => {
    expect(() => authConfigFromEnv({ TH_OIDC_ISSUER: 'http://id.example.invalid', TH_OIDC_CLIENT_ID: 'studio', NODE_ENV: 'production' } as any)).toThrow(/https/);
    // In production the session key is shared by reference, so every instance reads the same sessions (R-11-45).
    expect(() => authConfigFromEnv({ TH_OIDC_ISSUER: 'https://id.example.invalid', TH_OIDC_CLIENT_ID: 'studio', NODE_ENV: 'production' } as any)).toThrow(/TH_SESSION_KEY_REF/);
    expect(authConfigFromEnv({ TH_OIDC_ISSUER: 'https://id.example.invalid', TH_OIDC_CLIENT_ID: 'studio', NODE_ENV: 'production', TH_SESSION_KEY_REF: 'env:K', K: 'k'.repeat(32) } as any).issuer).toBe('https://id.example.invalid');
  });
});

describe('a tenant reads and runs only what is its own (R-06-01)', () => {
  it('a bank cannot read or run another bank\'s test, nor run a test for another tenant or with another store', async () => {
    expect((await as('example.maker', 'GET', '/api/tests/WPS-IN-001')).status).toBe(200);
    expect((await as('second.maker', 'GET', '/api/tests/WPS-IN-001')).status).toBe(404);
    // An override that names another tenant is refused; a run's configuration cannot carry the tenant or the store.
    const own = await as('second.maker', 'POST', '/api/tests/WPS-OUT-001/run', { override: { tenant: 'example-bank' } });
    expect(own.status).toBe(403);
    const cfg = await as('second.maker', 'POST', '/api/tests/WPS-OUT-001/run', { config: { tenant: 'example-bank', store: {}, scripts: {} } });
    expect(cfg.status).toBe(200);
  });
  it('a bank sees neither another bank\'s simulators nor its stubs, nor its artefacts in the data browser or the checks', async () => {
    const sims = (await as('second.maker', 'GET', '/api/simulators')).body.map((s: any) => s.name);
    expect(sims).not.toContain('b.sim.treasury.v1');
    expect((await as('example.maker', 'GET', '/api/simulators')).body.map((s: any) => s.name)).toContain('b.sim.treasury.v1');
    expect((await as('second.maker', 'GET', '/api/simulators/b.sim.treasury.v1/stub')).status).toBe(404);
    const issues = (await as('second.maker', 'GET', '/api/checks')).body.issues;
    expect(JSON.stringify(issues)).not.toMatch(/example-bank/);
    const meta = (await as('second.maker', 'GET', '/api/meta')).body;
    const mine = (await as('example.maker', 'GET', '/api/meta')).body;
    expect(meta.counts.simulator).toBeLessThan(mine.counts.simulator);
  });
  it('the kit and a reload of the branch are asked of the permission table', async () => {
    expect((await as('example.auditor', 'POST', '/api/kit/run', { only: [] })).status).toBe(403);
    expect((await as('example.maker', 'POST', '/api/git/reload', {})).status).toBe(403);
  });
});

describe('no pattern takes quadratic time on a long text (R-11-43)', () => {
  it('masks and checks a megabyte with no address in it in linear time', async () => {
    const { scrub } = await import('../src/telemetry/mask.js');
    const t = Date.now();
    scrub('a'.repeat(1_000_000));
    scrub('1'.repeat(1_000_000) + 'x');
    expect(Date.now() - t).toBeLessThan(3000);
    expect(scrub('write to someone@example.com')).toBe('write to [masked-email]');
  });
});

describe('paths and archives a caller names stay inside their bounds (R-09-59, R-11-44)', () => {
  it('a document, a script or a sample named with dots never leaves its folder on the branch', () => {
    for (const doc of [{ kind: 'activity', name: '..', release: '1.0.0' }, { kind: 'scheme', scope: '..', release: '1.0.0' }, { kind: 'test', id: '..', target: '..' }, { kind: 'activity', scope: '..', name: '.hidden' }]) {
      for (const tenant of ['owner', 'example-bank']) {
        const p = documentPath(doc, tenant);
        expect(p.split('/'), p).not.toContain('..');
        expect(p.split('/'), p).not.toContain('.');
      }
    }
    expect(scriptPath('example-bank', '..', 'typescript').split('/')).not.toContain('..');
    expect(() => loadSample({ samplesDirs: ['/tmp'], store: env.store } as any, 'samples/../../etc/passwd')).toThrow(/not a sample name/);
  });
  it('a zip bomb, or an archive of too many entries, is refused, not inflated', () => {
    const bomb = deflateRawSync(Buffer.alloc(4 * 1024 * 1024));
    const zip = oneEntry('bomb.txt', bomb, 8, 4 * 1024 * 1024);
    expect(unzip(zip, { entries: 10, entryBytes: 4 * 1024 * 1024, totalBytes: 8 * 1024 * 1024 })[0].content.length).toBe(4 * 1024 * 1024);
    expect(() => unzip(zip, { entries: 10, entryBytes: 1024 * 1024, totalBytes: 8 * 1024 * 1024 })).toThrow(ZipRefused);
    expect(() => unzip(zip, { entries: 0, entryBytes: 1024 * 1024, totalBytes: 8 * 1024 * 1024 })).toThrow(/entries/);
    expect(() => unzip(Buffer.concat([Buffer.alloc(10), zip.subarray(zip.length - 22)]))).toThrow(ZipRefused);
  });
  it('the import answers 422 to a bomb, never 500', async () => {
    const huge = oneEntry('artefacts/x.yaml', deflateRawSync(Buffer.alloc(80 * 1024 * 1024)), 8, 80 * 1024 * 1024);
    const r = await as('shared.designer', 'POST', '/api/import/preview', { bundle: huge.toString('base64') });
    expect(r.status).toBe(422);
    expect(r.body.error).toMatch(/refused/);
  });
  it('a download\'s file name cannot add a header parameter', () => {
    expect(safeFilename('export-temporal-p.wf.a.v1+b.zip')).toBe('export-temporal-p.wf.a.v1+b.zip');
    expect(safeFilename('x"; filename*=UTF-8\'\'evil.exe')).not.toMatch(/["';= ]/);
  });
});

/** A zip of one entry, as another writer would make it. */
function oneEntry(name: string, data: Buffer, method: number, size: number): Buffer {
  const n = Buffer.from(name);
  const local = Buffer.alloc(30);
  local.writeUInt32LE(0x04034b50, 0);
  local.writeUInt16LE(method, 8);
  local.writeUInt32LE(data.length, 18);
  local.writeUInt32LE(size, 22);
  local.writeUInt16LE(n.length, 26);
  const central = Buffer.alloc(46);
  central.writeUInt32LE(0x02014b50, 0);
  central.writeUInt16LE(method, 10);
  central.writeUInt32LE(data.length, 20);
  central.writeUInt32LE(size, 24);
  central.writeUInt16LE(n.length, 28);
  central.writeUInt32LE(0, 42);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(1, 8);
  end.writeUInt16LE(1, 10);
  end.writeUInt32LE(46 + n.length, 12);
  end.writeUInt32LE(30 + n.length + data.length, 16);
  return Buffer.concat([local, n, data, central, n, end]);
}
