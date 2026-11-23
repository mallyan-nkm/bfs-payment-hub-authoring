import type { AddressInfo } from 'node:net';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, describe, expect, it } from 'vitest';
import { authConfigFromEnv, CLI_TOKEN_PREFIX, DevProvider, Sealer, Sessions } from '../src/server/auth.js';
import { users } from '../src/governance/governance.js';
import { createStudioServer } from '../src/server/http.js';
import { api, login, logout, tokenFor } from '../src/cli-auth.js';
import { freshEnv } from './helpers.js';

// Sessions on several instances (R-11-45, Issue I-72) and the command line's sign-in (R-11-46, I-71).
// Each studio here is its own server on its own port, over one store, as two instances behind a load
// balancer would be; the session key is shared by reference.

const servers: { close: () => void }[] = [];
afterAll(() => servers.forEach((s) => s.close()));
const KEY = { TH_SESSION_KEY_REF: 'env:TH_TEST_SESSION_KEY', TH_TEST_SESSION_KEY: 'synthetic-session-key-for-tests-0123456789' };

/** Two studios at one public address (as behind one load balancer): each on its own port. */
async function instances(n: number, extra: Record<string, string> = {}) {
  const env = freshEnv();
  const ports: number[] = [];
  for (let i = 0; i < n; i++) {
    const probe = createStudioServer(env, authConfigFromEnv({ TH_AUTH_DEV: '1', NODE_ENV: 'test' } as any, 1));
    await new Promise<void>((r) => probe.listen(0, '127.0.0.1', r));
    ports.push((probe.address() as AddressInfo).port);
    probe.close();
  }
  // One public URL for all, the first instance's, and one development provider, as instances share one
  // identity provider. The callback of a sign-in may land on any instance.
  const pub = `http://127.0.0.1:${ports[0]}`;
  const urls: string[] = [];
  const devProvider = new DevProvider(`${pub}/auth/dev`, () => users(env).map((u) => u.subject ?? u.id));
  for (const port of ports) {
    const server = createStudioServer(env, authConfigFromEnv({ TH_AUTH_DEV: '1', NODE_ENV: 'test', TH_PUBLIC_URL: pub, ...KEY, ...extra } as any), { devProvider });
    await new Promise<void>((r) => server.listen(port, '127.0.0.1', r));
    servers.push(server);
    urls.push(`http://127.0.0.1:${port}`);
  }
  return urls;
}

/** Start a sign-in on one instance; finish the callback on another. */
async function signIn(start: string, finish: string, user: string) {
  const jar: string[] = [];
  const take = (r: Response) => r.headers.getSetCookie().forEach((c) => jar.push(c.split(';')[0]));
  const loginRes = await fetch(`${start}/auth/login`, { redirect: 'manual' });
  take(loginRes);
  const at = new URL(loginRes.headers.get('location')!);
  const form = new URLSearchParams(at.searchParams);
  form.set('user', user);
  const code = await fetch(`${start}${at.pathname}`, { method: 'POST', body: form, redirect: 'manual' });
  const back = new URL(code.headers.get('location')!);
  const cb = await fetch(`${finish}${back.pathname}${back.search}`, { redirect: 'manual', headers: { cookie: jar.join('; ') } });
  take(cb);
  return { status: cb.status, cookie: jar.filter((c) => c.startsWith('th_session=')).pop() };
}

describe('sessions on several instances (R-11-45)', () => {
  it('a sign-in started on one instance finishes on another, and either reads the session', async () => {
    const [a, b] = await instances(2);
    const s = await signIn(a, b, 'example.maker');
    expect(s.status).toBe(303);
    for (const u of [a, b]) {
      const me = await (await fetch(`${u}/auth/me`, { headers: { cookie: s.cookie! } })).json();
      expect(me).toMatchObject({ signedIn: true, user: 'example.maker' });
      expect((await fetch(`${u}/api/me`, { headers: { cookie: s.cookie! } })).status).toBe(200);
    }
  });

  it('keeps no session in memory: an instance started afterwards with the same key reads it, one with another key does not', async () => {
    const [a] = await instances(1);
    const s = await signIn(a, a, 'example.maker');
    const [later] = await instances(1);
    expect((await fetch(`${later}/api/me`, { headers: { cookie: s.cookie! } })).status).toBe(200);
    const [other] = await instances(1, { TH_TEST_SESSION_KEY: 'another-synthetic-session-key-0123456789' });
    expect((await fetch(`${other}/api/me`, { headers: { cookie: s.cookie! } })).status).toBe(401);
  });

  it('seals what a cookie says: a changed byte, or a session cookie offered as a sign-in, is refused', () => {
    const cfg = authConfigFromEnv({ TH_AUTH_DEV: '1', NODE_ENV: 'test', ...KEY } as any, 1);
    const sessions = new Sessions(cfg);
    const cookie = sessions.sessionCookie(sessions.create('example.maker', 'example.maker')).split(';')[0];
    expect(sessions.fromCookieHeader(cookie)?.user).toBe('example.maker');
    const value = decodeURIComponent(cookie.slice('th_session='.length));
    const flipped = value.slice(0, 20) + (value[20] === 'A' ? 'B' : 'A') + value.slice(21);
    expect(sessions.fromCookieHeader(`th_session=${encodeURIComponent(flipped)}`)).toBeUndefined();
    // Purposes are separate keys: a session cookie is no sign-in in progress and no command line token.
    expect(sessions.finish('x', `th_login=${encodeURIComponent(value)}`)).toBeUndefined();
    expect(sessions.fromCliToken(`Bearer ${CLI_TOKEN_PREFIX}${value}`)).toBeUndefined();
    // The cookie does not show the user in clear text.
    expect(Buffer.from(value.slice(3), 'base64url').toString('latin1')).not.toContain('example.maker');
    expect(new Sealer(Buffer.from('x'.repeat(32))).open('session', value)).toBeUndefined();
  });

  it('signs out on the instance asked at once, and the cookie is cleared', async () => {
    const [a] = await instances(1);
    const s = await signIn(a, a, 'example.maker');
    const me = await (await fetch(`${a}/auth/me`, { headers: { cookie: s.cookie! } })).json();
    const out = await fetch(`${a}/auth/logout`, { method: 'POST', headers: { cookie: s.cookie!, 'x-csrf-token': me.csrfToken } });
    expect(out.status).toBe(200);
    expect(out.headers.getSetCookie().join(';')).toMatch(/th_session=;.*Max-Age=0/);
    expect((await fetch(`${a}/api/me`, { headers: { cookie: s.cookie! } })).status).toBe(401);
  });
});

describe('the command line signs in by device authorization (R-11-46)', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'th-cli-token-'));
  afterAll(() => fs.rmSync(tmp, { recursive: true, force: true }));

  /** The operator's part: open the page the command line names, enter its code and choose the user. */
  const approve = (user: string) => async (line: string) => {
    const m = /open (\S+) and enter the code (\S+)/.exec(line);
    if (!m) return;
    await fetch(m[1], { method: 'POST', body: new URLSearchParams({ user_code: m[2], user }) });
  };

  it('signs in, keeps its token for its owner only, and the permission table decides its calls', async () => {
    const [a, b] = await instances(2);
    const env = { TH_STUDIO_TOKEN_FILE: path.join(tmp, 'designer.json') } as any;
    const say = approve('workflow.designer');
    const t = await login(a, { env, say: (l) => void say(l), timeoutMs: 20_000 });
    expect(t).toMatchObject({ user: 'workflow.designer', tenant: 'owner' });
    expect(t.roles).toContain('workflow designer');
    expect(fs.statSync(env.TH_STUDIO_TOKEN_FILE).mode & 0o077).toBe(0);
    expect(tokenFor(a, env)).toMatch(/^thcli\./);
    expect((await api(a, 'GET', '/api/me', undefined, env)).user).toBe('workflow.designer');
    // The token is sealed with the shared key: another instance takes it as well.
    const r = await fetch(`${b}/api/me`, { headers: { authorization: `Bearer ${tokenFor(a, env)}` } });
    expect(r.status).toBe(200);
    // A designer builds no activity: refused by the table, as it is for the web app; no CSRF token is asked.
    const activity = { kind: 'activity', name: 'p.enrich.cli-made.v1', release: '1.0.0', title: 'CLI test', type: 'enrich', reads: ['header'], writes: ['facts.cliTest'], outcomes: [{ name: 'done', primary: true }], effect: 'pure', implementation: { binding: 'builtin', service: 'noop' }, provenance: { origin: 'authored', status: 'provisional' } };
    await expect(api(a, 'POST', '/api/drafts', { doc: activity }, env)).rejects.toThrow(/^403/);
    // The acting user is the token's: a body naming another is refused.
    await expect(api(a, 'POST', '/api/review', { key: 'x', actor: 'shared.checker' }, env)).rejects.toThrow(/signed-in user, workflow\.designer/);
    // Signed out, the studio refuses the token and the saved one is gone.
    const token = tokenFor(a, env);
    await logout(a, env);
    expect(fs.existsSync(env.TH_STUDIO_TOKEN_FILE)).toBe(false);
    expect((await fetch(`${a}/api/me`, { headers: { authorization: `Bearer ${token}` } })).status).toBe(401);
  });

  it('is pending until the operator finishes, refuses an unknown device code, and takes no other bearer token', async () => {
    const [a] = await instances(1);
    const d = await (await fetch(`${a}/auth/device`, { method: 'POST' })).json();
    expect(d).toMatchObject({ verification_uri: expect.stringContaining('/auth/dev/device'), interval: 1 });
    const poll = () => fetch(`${a}/auth/device/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ device_code: d.device_code }) });
    expect((await poll()).status).toBe(428);
    expect((await fetch(`${a}/auth/device/token`, { method: 'POST', body: JSON.stringify({ device_code: 'unknown' }) })).status).toBe(401);
    await fetch(d.verification_uri, { method: 'POST', body: new URLSearchParams({ user_code: d.user_code, user: 'example.maker' }) });
    const done = await poll();
    expect(done.status).toBe(200);
    const { token, user } = await done.json();
    expect(user).toBe('example.maker');
    // Used once: the provider gave the device code's tokens already.
    expect((await poll()).status).toBe(401);
    expect((await fetch(`${a}/api/me`, { headers: { authorization: `Bearer ${token}` } })).status).toBe(200);
    expect((await fetch(`${a}/api/me`, { headers: { authorization: 'Bearer some-other-token' } })).status).toBe(401);
    expect((await fetch(`${a}/api/me`, { headers: { authorization: `Bearer ${token.slice(0, -2)}xx` } })).status).toBe(401);
  });
});
