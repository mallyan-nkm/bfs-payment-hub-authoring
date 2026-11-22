import type { AddressInfo } from 'node:net';
import { afterAll, describe, expect, it } from 'vitest';
import { AuthConfigError, DevProvider, OidcClient, authConfigFromEnv, resolveSecretRef, sign, unsign } from '../src/server/auth.js';
import { createStudioServer } from '../src/server/http.js';
import { freshEnv } from './helpers.js';

// Sign-in, in process (R-11-32 to R-11-34): the configuration, the cookie signature, the ID token
// checks, the development provider's guard, bearer tokens and the provider's groups.

const servers: { close: () => void }[] = [];
afterAll(() => servers.forEach((s) => s.close()));

async function start(extra: Record<string, string> = {}) {
  const env = freshEnv();
  const probe = createStudioServer(env, authConfigFromEnv({ TH_AUTH_DEV: '1', NODE_ENV: 'test' } as any, 1));
  await new Promise<void>((r) => probe.listen(0, '127.0.0.1', r));
  const port = (probe.address() as AddressInfo).port;
  probe.close();
  const url = `http://127.0.0.1:${port}`;
  const server = createStudioServer(env, authConfigFromEnv({ TH_AUTH_DEV: '1', NODE_ENV: 'test', TH_PUBLIC_URL: url, ...extra } as any));
  await new Promise<void>((r) => server.listen(port, '127.0.0.1', r));
  servers.push(server);
  return url;
}

/** Sign in through the development provider, with groups if given. */
async function signIn(base: string, user: string, groups?: string) {
  const jar: string[] = [];
  const take = (r: Response) => r.headers.getSetCookie().forEach((c) => jar.push(c.split(';')[0]));
  const login = await fetch(`${base}/auth/login`, { redirect: 'manual' });
  take(login);
  const at = new URL(login.headers.get('location')!);
  const form = new URLSearchParams(at.searchParams);
  form.set('user', user);
  if (groups) form.set('groups', groups);
  const code = await fetch(`${at.origin}${at.pathname}`, { method: 'POST', body: form, redirect: 'manual' });
  const cb = await fetch(code.headers.get('location')!, { redirect: 'manual', headers: { cookie: jar.join('; ') } });
  take(cb);
  const cookie = jar.filter((c) => c.startsWith('th_session=')).pop()!;
  return { cookie, me: await (await fetch(`${base}/auth/me`, { headers: { cookie } })).json() };
}

describe('the sign-in configuration (R-11-32)', () => {
  it('needs an identity provider or the development provider, and refuses the development provider in production', () => {
    expect(() => authConfigFromEnv({} as any)).toThrow(AuthConfigError);
    expect(() => authConfigFromEnv({ TH_AUTH_DEV: '1', NODE_ENV: 'production' } as any)).toThrow(/refused when NODE_ENV=production/);
    expect(() => authConfigFromEnv({ TH_AUTH_DEV: '1', TH_OIDC_ISSUER: 'https://id.example.invalid' } as any)).toThrow(/not both/);
    const c = authConfigFromEnv({ TH_OIDC_ISSUER: 'https://id.example.invalid/', TH_OIDC_CLIENT_ID: 'studio', TH_OIDC_CLIENT_SECRET_REF: 'env:STUDIO_SECRET', STUDIO_SECRET: 'synthetic-secret', TH_PUBLIC_URL: 'https://studio.example.invalid' } as any);
    expect(c).toMatchObject({ issuer: 'https://id.example.invalid', clientId: 'studio', clientSecret: 'synthetic-secret', redirectUri: 'https://studio.example.invalid/auth/callback', secureCookies: true, dev: false });
  });

  it('reads a secret only by reference, env:<NAME>', () => {
    expect(resolveSecretRef('env:X', { X: 'v' } as any)).toBe('v');
    expect(() => resolveSecretRef('plain-secret', {} as any)).toThrow(/by reference/);
    expect(() => resolveSecretRef('env:MISSING', {} as any)).toThrow(/not set/);
    expect(() => authConfigFromEnv({ TH_AUTH_DEV: '1', TH_SESSION_KEY_REF: 'env:K', K: 'short' } as any)).toThrow(/32 characters/);
  });

  it('signs a cookie value and refuses one that was changed', () => {
    const key = Buffer.from('k'.repeat(32));
    const s = sign(key, 'abc');
    expect(unsign(key, s)).toBe('abc');
    expect(unsign(key, s.replace('abc', 'abd'))).toBeUndefined();
    expect(unsign(Buffer.from('j'.repeat(32)), s)).toBeUndefined();
    expect(unsign(key, 'abc')).toBeUndefined();
  });
});

describe('the ID token (R-11-32, R-11-34)', () => {
  const issuer = 'http://studio.test/auth/dev';
  const dev = new DevProvider(issuer, () => []);
  const fetchFn: typeof fetch = async (input, init) => {
    const out = dev.handle(init?.method ?? 'GET', new URL(String(input)), undefined)!;
    return new Response(out.body, { status: out.status, headers: out.headers });
  };
  const cfg = (clientId: string, iss = issuer) => authConfigFromEnv({ TH_OIDC_ISSUER: iss, TH_OIDC_CLIENT_ID: clientId } as any);
  const now = Math.floor(Date.now() / 1000);
  const good = { iss: issuer, aud: 'studio-dev', sub: 'x', iat: now, exp: now + 60 };

  it('accepts a token signed by the studio\'s provider, for the studio', async () => {
    expect((await new OidcClient(cfg('studio-dev'), fetchFn).verifyIdToken(dev.sign(good))).sub).toBe('x');
  });
  it('refuses a token for another client, from another issuer, expired, or with a changed body', async () => {
    const c = new OidcClient(cfg('studio-dev'), fetchFn);
    await expect(new OidcClient(cfg('runtime-api'), fetchFn).verifyIdToken(dev.sign(good))).rejects.toThrow(/another client/);
    await expect(c.verifyIdToken(dev.sign({ ...good, iss: 'http://runtime.test' }))).rejects.toThrow(/another issuer/);
    await expect(c.verifyIdToken(dev.sign({ ...good, exp: now - 3600 }))).rejects.toThrow(/expired/);
    const [h, , s] = dev.sign(good).split('.');
    const forged = `${h}.${Buffer.from(JSON.stringify({ ...good, sub: 'admin' })).toString('base64url')}.${s}`;
    await expect(c.verifyIdToken(forged)).rejects.toThrow(/signature/);
    const none = `${Buffer.from(JSON.stringify({ alg: 'none' })).toString('base64url')}.${Buffer.from(JSON.stringify(good)).toString('base64url')}.`;
    await expect(c.verifyIdToken(none)).rejects.toThrow(/does not accept/);
  });
});

describe('the server (R-11-32 to R-11-34)', () => {
  it('accepts no bearer token on the API, from the runtime or anyone', async () => {
    const url = await start();
    const r = await fetch(`${url}/api/catalogue`, { headers: { authorization: 'Bearer a-runtime-token' } });
    expect(r.status).toBe(401);
    expect((await r.json()).code).toBe('R-11-34');
  });

  it('narrows a user\'s roles to the groups the identity provider asserts, and never adds one', async () => {
    const url = await start({ TH_OIDC_GROUPS_CLAIM: 'groups' });
    const both = await signIn(url, 'shared.designer', 'studio:workflow designer,studio:platform approver');
    expect(both.me.roles).toEqual(['workflow designer']);
    const r = await fetch(`${url}/api/me`, { headers: { cookie: both.cookie } });
    expect((await r.json()).roles).toEqual(['workflow designer']);
    const none = await signIn(url, 'shared.designer');
    expect(none.me.roles).toEqual([]);
    expect(none.me.surfaces).toEqual([]);
  });

  it('without a groups claim, takes the roles from governance/users.yaml alone', async () => {
    const url = await start();
    expect((await signIn(url, 'shared.designer', 'studio:platform approver')).me.roles).toEqual(['workflow designer', 'workflow developer']);
  });
});
