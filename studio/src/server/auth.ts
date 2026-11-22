import { createCipheriv, createDecipheriv, createHash, createHmac, createPublicKey, generateKeyPairSync, hkdfSync, randomBytes, timingSafeEqual, verify as verifySignature, createSign, type KeyObject } from 'node:crypto';

// Sign-in for the studio (R-11-32, R-11-33, Issue I-65). OpenID Connect, authorization code with PKCE,
// against the identity provider the environment names. The session is a sealed (AES-256-GCM), http-only,
// same-site cookie that carries its own short-lived claims, so every instance with the same session key
// reads it and nothing is held in one process or written to disk (R-11-24, R-11-45, Issue I-72). Every
// state-changing call from a browser carries the session's CSRF token. The command line signs in by the
// device authorization flow against the same provider and gets a sealed token of its own (R-11-46, I-71).
//
// For local work and tests, TH_AUTH_DEV=1 starts a built-in development provider at /auth/dev: a small
// OpenID provider in this process whose sign-in page lists the studio's users. It refuses to start
// when NODE_ENV is production.

export interface AuthConfig {
  issuer: string;
  clientId: string;
  clientSecret?: string;
  redirectUri?: string;
  scopes: string;
  /** The ID token claim matched to a user's `subject`, or else its `id`. */
  userClaim: string;
  /**
   * Optional: the ID token claim that lists the identity provider's groups. When set, a user holds a
   * role of governance/users.yaml only while the token also asserts the group `<groupPrefix><role>`
   * (R-11-34). The provider can narrow a user's roles this way; it never grants one.
   */
  groupsClaim?: string;
  groupPrefix: string;
  dev: boolean;
  secureCookies: boolean;
  sessionHours: number;
  /** A session unused this long ends (R-11-42): TH_SESSION_IDLE_MINUTES, 60 by default. */
  idleMinutes: number;
  /** The client the command line signs in as (device authorization, R-11-46): TH_OIDC_CLI_CLIENT_ID, else the studio's. */
  cliClientId: string;
  /** How long a command line's token lasts: TH_CLI_SESSION_HOURS, 8 by default. */
  cliHours: number;
  /** Whether the session key came from TH_SESSION_KEY_REF, so other instances and a restart read the same sessions. */
  sharedKey: boolean;
  key: Buffer;
}

export class AuthConfigError extends Error {}

/** A `secretRef`: `env:NAME` reads the variable NAME. The value is never logged or returned. */
export function resolveSecretRef(ref: string | undefined, env: NodeJS.ProcessEnv = process.env): string | undefined {
  if (!ref) return undefined;
  const m = /^env:([A-Za-z_][A-Za-z0-9_]*)$/.exec(ref.trim());
  if (!m) throw new AuthConfigError(`A secret is named by reference, env:<NAME>; ${ref.includes(':') ? ref.split(':')[0] + ':' : 'this'} is not a reference the studio reads`);
  const v = env[m[1]];
  if (!v) throw new AuthConfigError(`The secret reference ${ref} names an environment variable that is not set`);
  return v;
}

/** The sign-in configuration from the environment, or why the studio cannot start. */
export function authConfigFromEnv(env: NodeJS.ProcessEnv = process.env, port?: number): AuthConfig {
  const dev = env.TH_AUTH_DEV === '1';
  if (dev && env.NODE_ENV === 'production') throw new AuthConfigError('TH_AUTH_DEV=1 is for local development and tests; it is refused when NODE_ENV=production');
  if (dev && env.TH_OIDC_ISSUER) throw new AuthConfigError('Set either TH_OIDC_ISSUER or TH_AUTH_DEV=1, not both');
  if (!dev && (!env.TH_OIDC_ISSUER || !env.TH_OIDC_CLIENT_ID)) {
    throw new AuthConfigError('The studio needs sign-in: set TH_OIDC_ISSUER and TH_OIDC_CLIENT_ID (and TH_OIDC_CLIENT_SECRET_REF=env:<NAME> if the provider wants a client secret), or TH_AUTH_DEV=1 for local development');
  }
  const base = env.TH_PUBLIC_URL ?? `http://localhost:${port ?? env.PORT ?? 4317}`;
  // In production the identity provider, its keys and the studio itself are reached over TLS only (R-11-43).
  if (!dev && env.NODE_ENV === 'production' && !/^https:\/\//.test(env.TH_OIDC_ISSUER!)) throw new AuthConfigError('In production TH_OIDC_ISSUER is an https:// URL');
  const keyRef = env.TH_SESSION_KEY_REF;
  // In production every instance seals sessions with the same key, by reference (R-11-45): without one, a
  // second instance would not read the first one's sessions and a restart would sign everyone out.
  if (!dev && env.NODE_ENV === 'production' && !keyRef) throw new AuthConfigError('In production set TH_SESSION_KEY_REF=env:<NAME>: every studio instance seals sessions with that key (R-11-45)');
  const keyText = resolveSecretRef(keyRef, env);
  if (keyText !== undefined && keyText.length < 32) throw new AuthConfigError('The session key needs at least 32 characters');
  return {
    issuer: dev ? `${base.replace(/\/$/, '')}/auth/dev` : env.TH_OIDC_ISSUER!.replace(/\/$/, ''),
    clientId: dev ? 'studio-dev' : env.TH_OIDC_CLIENT_ID!,
    clientSecret: dev ? undefined : resolveSecretRef(env.TH_OIDC_CLIENT_SECRET_REF, env),
    redirectUri: env.TH_OIDC_REDIRECT_URI ?? `${base.replace(/\/$/, '')}/auth/callback`,
    scopes: env.TH_OIDC_SCOPES ?? 'openid profile email',
    userClaim: dev ? 'sub' : env.TH_OIDC_USER_CLAIM ?? 'sub',
    groupsClaim: env.TH_OIDC_GROUPS_CLAIM || undefined,
    groupPrefix: env.TH_OIDC_GROUP_PREFIX ?? 'studio:',
    dev,
    secureCookies: env.TH_COOKIE_SECURE === '1' || base.startsWith('https://'),
    sessionHours: Number(env.TH_SESSION_HOURS ?? 8),
    idleMinutes: Number(env.TH_SESSION_IDLE_MINUTES ?? 60),
    cliClientId: dev ? 'studio-dev' : env.TH_OIDC_CLI_CLIENT_ID ?? env.TH_OIDC_CLIENT_ID!,
    cliHours: Number(env.TH_CLI_SESSION_HOURS ?? 8),
    sharedKey: !!keyText,
    // Without a key reference the key lives for the process: a restart signs everyone out.
    key: keyText ? Buffer.from(keyText, 'utf8') : randomBytes(32),
  };
}

// ------------------------------------------------------------------- cookies

export const SESSION_COOKIE = 'th_session';
const LOGIN_COOKIE = 'th_login';

export function parseCookies(header: string | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  for (const part of (header ?? '').split(';')) {
    const i = part.indexOf('=');
    if (i <= 0) continue;
    // A cookie that does not decode is not ours: it is skipped, not an error.
    try {
      out[part.slice(0, i).trim()] = decodeURIComponent(part.slice(i + 1).trim());
    } catch {
      /* skipped */
    }
  }
  return out;
}

const b64u = (b: Buffer) => b.toString('base64url');

export function sign(key: Buffer, value: string): string {
  return `${value}.${b64u(createHmac('sha256', key).update(value).digest())}`;
}

export function unsign(key: Buffer, signed: string | undefined): string | undefined {
  if (!signed) return undefined;
  const i = signed.lastIndexOf('.');
  if (i <= 0) return undefined;
  const value = signed.slice(0, i);
  const want = Buffer.from(sign(key, value).slice(i + 1));
  const got = Buffer.from(signed.slice(i + 1));
  return want.length === got.length && timingSafeEqual(want, got) ? value : undefined;
}

function cookie(name: string, value: string, cfg: AuthConfig, maxAgeSeconds: number) {
  return `${name}=${encodeURIComponent(value)}; Path=/; HttpOnly; SameSite=Lax; Max-Age=${maxAgeSeconds}${cfg.secureCookies ? '; Secure' : ''}`;
}

// ------------------------------------------------------------------ sessions

/**
 * A session (R-11-32, R-11-45). It lives in its sealed cookie, not in the server: every instance that
 * holds the same session key reads it, and a restart signs nobody out (Issue I-72,
 * docs/adr/0002-stateless-sessions.md). `renew` is set when the cookie should be sent again, so the idle
 * time slides.
 */
export interface Session { id: string; subject: string; user?: string; csrf: string; expires: number; groups?: string[]; lastSeen?: number; renew?: boolean; via?: 'browser' | 'cli' }

/** A sign-in in progress is held in its browser's sealed cookie, not in the server (R-11-41, R-11-45); kept for callers that size it. */
export const MAX_PENDING_SIGN_INS = 0;

/** How often a used session's cookie is sent again, so its idle time slides without a header on every answer. */
const RENEW_AFTER_MS = 60_000;

/** The prefix of a command line's token (R-11-46): the studio's own, sealed with its session key. */
export const CLI_TOKEN_PREFIX = 'thcli.';

/**
 * Seal and open a value with AES-256-GCM under a key derived from the session key for one purpose, so
 * a session cookie, a sign-in cookie and a command line token are never taken for one another.
 */
export class Sealer {
  private keys = new Map<string, Buffer>();
  constructor(private key: Buffer) {}

  private keyFor(purpose: string) {
    let k = this.keys.get(purpose);
    if (!k) {
      k = Buffer.from(hkdfSync('sha256', this.key, Buffer.from('transacthub-studio'), Buffer.from(`th:${purpose}:v1`), 32));
      this.keys.set(purpose, k);
    }
    return k;
  }

  seal(purpose: string, value: unknown): string {
    const iv = randomBytes(12);
    const c = createCipheriv('aes-256-gcm', this.keyFor(purpose), iv);
    const body = Buffer.concat([c.update(JSON.stringify(value), 'utf8'), c.final()]);
    return `v1.${b64u(Buffer.concat([iv, body, c.getAuthTag()]))}`;
  }

  open<T = any>(purpose: string, sealed: string | undefined): T | undefined {
    if (!sealed || !sealed.startsWith('v1.')) return undefined;
    try {
      const raw = Buffer.from(sealed.slice(3), 'base64url');
      if (raw.length < 29) return undefined;
      const d = createDecipheriv('aes-256-gcm', this.keyFor(purpose), raw.subarray(0, 12));
      d.setAuthTag(raw.subarray(raw.length - 16));
      return JSON.parse(Buffer.concat([d.update(raw.subarray(12, raw.length - 16)), d.final()]).toString('utf8')) as T;
    } catch {
      return undefined;
    }
  }
}

interface SessionClaims { sid: string; sub: string; user?: string; csrf: string; exp: number; seen: number; groups?: string[] }
interface LoginClaims { state: string; verifier: string; nonce: string; returnTo: string; exp: number }
interface CliClaims { sid: string; sub: string; user?: string; exp: number; groups?: string[] }

/**
 * Sessions and sign-ins in progress, held in sealed cookies (Issue I-72). The server keeps only the ids
 * of the sessions signed out on this instance until they would have expired, so a signed-out cookie is
 * refused here at once; another instance refuses it at its idle time (docs/adr/0002-stateless-sessions.md).
 */
export class Sessions {
  private sealer: Sealer;
  private signedOut = new Map<string, number>();

  constructor(private cfg: AuthConfig) {
    this.sealer = new Sealer(cfg.key);
  }

  create(subject: string, user: string | undefined, groups?: string[]): Session {
    this.sweep();
    const now = Date.now();
    return { id: b64u(randomBytes(18)), subject, user, csrf: b64u(randomBytes(24)), expires: now + this.cfg.sessionHours * 3600_000, groups, lastSeen: now, via: 'browser' };
  }

  fromCookieHeader(header: string | undefined): Session | undefined {
    const c = this.sealer.open<SessionClaims>('session', parseCookies(header)[SESSION_COOKIE]);
    if (!c || typeof c.sid !== 'string' || typeof c.sub !== 'string' || typeof c.exp !== 'number' || typeof c.seen !== 'number') return undefined;
    const now = Date.now();
    const idle = (this.cfg.idleMinutes ?? 60) * 60_000;
    if (c.exp < now || (idle > 0 && now - c.seen > idle) || this.signedOut.has(c.sid)) return undefined;
    return { id: c.sid, subject: c.sub, user: c.user, csrf: c.csrf, expires: c.exp, groups: c.groups, lastSeen: c.seen, renew: now - c.seen > RENEW_AFTER_MS, via: 'browser' };
  }

  /** Sign-out: the cookie is cleared, and this instance refuses the session from now on. */
  destroy(id: string, expires = Date.now() + this.cfg.sessionHours * 3600_000) {
    this.sweep();
    this.signedOut.set(id, expires);
  }

  sessionCookie(s: Session) {
    const now = Date.now();
    const claims: SessionClaims = { sid: s.id, sub: s.subject, ...(s.user ? { user: s.user } : {}), csrf: s.csrf, exp: s.expires, seen: now, ...(s.groups ? { groups: s.groups } : {}) };
    return cookie(SESSION_COOKIE, this.sealer.seal('session', claims), this.cfg, Math.max(0, Math.floor((s.expires - now) / 1000)));
  }

  clearCookie() {
    return cookie(SESSION_COOKIE, '', this.cfg, 0);
  }

  begin(returnTo: string) {
    const state = b64u(randomBytes(24));
    const verifier = b64u(randomBytes(32));
    const nonce = b64u(randomBytes(16));
    const claims: LoginClaims = { state, verifier, nonce, returnTo, exp: Date.now() + 10 * 60_000 };
    return { state, verifier, nonce, challenge: b64u(createHash('sha256').update(verifier).digest()), cookie: cookie(LOGIN_COOKIE, this.sealer.seal('login', claims), this.cfg, 600) };
  }

  /** The sign-in the callback finishes: its state must match the one sealed in the browser's cookie, unexpired. */
  finish(state: string | undefined, cookieHeader: string | undefined) {
    const p = this.sealer.open<LoginClaims>('login', parseCookies(cookieHeader)[LOGIN_COOKIE]);
    if (!state || !p || typeof p.state !== 'string' || !same(p.state, state)) return undefined;
    return p.exp >= Date.now() ? { verifier: p.verifier, nonce: p.nonce, expires: p.exp, returnTo: p.returnTo } : undefined;
  }

  clearLoginCookie() {
    return cookie(LOGIN_COOKIE, '', this.cfg, 0);
  }

  /** For tests: what this instance holds in memory about sign-ins in progress. Nothing. */
  get pendingCount() {
    return 0;
  }

  /** A command line's token (R-11-46), after a device authorization sign-in: sealed, with its own expiry. */
  cliToken(subject: string, user: string | undefined, groups?: string[]): { token: string; expires: number; id: string } {
    const id = b64u(randomBytes(18));
    const exp = Date.now() + this.cfg.cliHours * 3600_000;
    const claims: CliClaims = { sid: id, sub: subject, ...(user ? { user } : {}), exp, ...(groups ? { groups } : {}) };
    return { token: CLI_TOKEN_PREFIX + this.sealer.seal('cli', claims), expires: exp, id };
  }

  /** The session a command line's `Authorization: Bearer thcli.…` stands for, or undefined. */
  fromCliToken(authorization: string | undefined): Session | undefined {
    const m = /^Bearer\s+(\S+)$/i.exec(authorization ?? '');
    if (!m || !m[1].startsWith(CLI_TOKEN_PREFIX)) return undefined;
    const c = this.sealer.open<CliClaims>('cli', m[1].slice(CLI_TOKEN_PREFIX.length));
    if (!c || typeof c.sid !== 'string' || typeof c.sub !== 'string' || typeof c.exp !== 'number') return undefined;
    if (c.exp < Date.now() || this.signedOut.has(c.sid)) return undefined;
    // A command line sends no cookie, so a CSRF token has nothing to protect: it gets none.
    return { id: c.sid, subject: c.sub, user: c.user, csrf: '', expires: c.exp, groups: c.groups, via: 'cli' };
  }

  private sweep() {
    const now = Date.now();
    for (const [k, exp] of this.signedOut) if (exp < now) this.signedOut.delete(k);
  }
}

function same(a: string, b: string) {
  const x = Buffer.from(a);
  const y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
}

// --------------------------------------------------------------------- OIDC client

interface Discovery { issuer: string; authorization_endpoint: string; token_endpoint: string; jwks_uri: string; end_session_endpoint?: string; device_authorization_endpoint?: string }

/** The answer to a device authorization request (RFC 8628, 3.2). */
export interface DeviceAuthorization { device_code: string; user_code: string; verification_uri: string; verification_uri_complete?: string; expires_in: number; interval?: number }

/** A device sign-in the user has not finished yet, or has refused: the provider's own error code. */
export class DevicePending extends Error {
  constructor(public code: string) {
    super(code);
  }
}

export class OidcClient {
  private discovery?: Discovery;
  private jwks = new Map<string, KeyObject>();

  constructor(private cfg: AuthConfig, private fetchFn: typeof fetch = fetch) {}

  async discover(): Promise<Discovery> {
    if (this.discovery) return this.discovery;
    const r = await this.fetchFn(`${this.cfg.issuer}/.well-known/openid-configuration`);
    if (!r.ok) throw new Error(`The identity provider's discovery document did not load (${r.status})`);
    const d = (await r.json()) as Discovery;
    if (d.issuer.replace(/\/$/, '') !== this.cfg.issuer) throw new Error('The identity provider names another issuer');
    this.discovery = d;
    return d;
  }

  async authorizeUrl(p: { state: string; nonce: string; challenge: string }) {
    const d = await this.discover();
    const u = new URL(d.authorization_endpoint);
    u.searchParams.set('response_type', 'code');
    u.searchParams.set('client_id', this.cfg.clientId);
    u.searchParams.set('redirect_uri', this.cfg.redirectUri!);
    u.searchParams.set('scope', this.cfg.scopes);
    u.searchParams.set('state', p.state);
    u.searchParams.set('nonce', p.nonce);
    u.searchParams.set('code_challenge', p.challenge);
    u.searchParams.set('code_challenge_method', 'S256');
    return u.toString();
  }

  /** Exchange the code, verify the ID token, and return its claims. */
  async redeem(code: string, verifier: string, nonce: string): Promise<{ claims: Record<string, any>; idToken: string }> {
    const d = await this.discover();
    const body = new URLSearchParams({ grant_type: 'authorization_code', code, redirect_uri: this.cfg.redirectUri!, client_id: this.cfg.clientId, code_verifier: verifier });
    if (this.cfg.clientSecret) body.set('client_secret', this.cfg.clientSecret);
    const r = await this.fetchFn(d.token_endpoint, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body });
    if (!r.ok) throw new Error(`The identity provider refused the code (${r.status})`);
    const tok = (await r.json()) as { id_token?: string };
    if (!tok.id_token) throw new Error('The identity provider sent no ID token');
    const claims = await this.verifyIdToken(tok.id_token);
    if (claims.nonce !== nonce) throw new Error('The ID token does not carry this sign-in\'s nonce');
    return { claims, idToken: tok.id_token };
  }

  /** Start a device authorization sign-in for the command line (RFC 8628, R-11-46). */
  async deviceAuthorization(): Promise<DeviceAuthorization> {
    const d = await this.discover();
    if (!d.device_authorization_endpoint) throw new Error('The identity provider offers no device authorization endpoint');
    const body = new URLSearchParams({ client_id: this.cfg.cliClientId, scope: this.cfg.scopes });
    const r = await this.fetchFn(d.device_authorization_endpoint, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body });
    if (!r.ok) throw new Error(`The identity provider refused the device authorization (${r.status})`);
    return (await r.json()) as DeviceAuthorization;
  }

  /** Poll for the device sign-in's tokens; verify the ID token, issued for the command line's client. */
  async redeemDevice(deviceCode: string): Promise<{ claims: Record<string, any> }> {
    const d = await this.discover();
    const body = new URLSearchParams({ grant_type: 'urn:ietf:params:oauth:grant-type:device_code', device_code: deviceCode, client_id: this.cfg.cliClientId });
    const r = await this.fetchFn(d.token_endpoint, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body });
    const tok = (await r.json().catch(() => ({}))) as { id_token?: string; error?: string };
    if (!r.ok) {
      const code = String(tok.error ?? '');
      if (['authorization_pending', 'slow_down', 'access_denied', 'expired_token'].includes(code)) throw new DevicePending(code);
      throw new Error(`The identity provider refused the device code (${r.status})`);
    }
    if (!tok.id_token) throw new Error('The identity provider sent no ID token');
    return { claims: await this.verifyIdToken(tok.id_token, this.cfg.cliClientId) };
  }

  async verifyIdToken(jwt: string, audience: string = this.cfg.clientId): Promise<Record<string, any>> {
    const parts = jwt.split('.');
    if (parts.length !== 3) throw new Error('The ID token is not a JWT');
    const header = JSON.parse(Buffer.from(parts[0], 'base64url').toString('utf8'));
    const claims = JSON.parse(Buffer.from(parts[1], 'base64url').toString('utf8'));
    const alg = header.alg;
    if (!['RS256', 'ES256', 'PS256'].includes(alg)) throw new Error(`The ID token is signed with ${alg}, which the studio does not accept`);
    const key = await this.key(header.kid);
    const data = Buffer.from(`${parts[0]}.${parts[1]}`);
    const sig = Buffer.from(parts[2], 'base64url');
    const ok = alg === 'ES256'
      ? verifySignature('sha256', data, { key, dsaEncoding: 'ieee-p1363' }, sig)
      : alg === 'PS256'
        ? verifySignature('sha256', data, { key, padding: 6, saltLength: 32 }, sig)
        : verifySignature('sha256', data, key, sig);
    if (!ok) throw new Error('The ID token\'s signature does not verify');
    const now = Math.floor(Date.now() / 1000);
    if (String(claims.iss).replace(/\/$/, '') !== this.cfg.issuer) throw new Error('The ID token is from another issuer');
    const aud = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
    if (!aud.includes(audience)) throw new Error('The ID token is for another client');
    if (typeof claims.exp !== 'number' || claims.exp + 60 < now) throw new Error('The ID token has expired');
    if (typeof claims.iat === 'number' && claims.iat - 300 > now) throw new Error('The ID token is issued in the future');
    if (typeof claims.nbf === 'number' && claims.nbf - 60 > now) throw new Error('The ID token is not valid yet');
    return claims;
  }

  private async key(kid: string | undefined): Promise<KeyObject> {
    const pick = () => (kid ? this.jwks.get(kid) : this.jwks.values().next().value);
    if (!pick()) {
      const d = await this.discover();
      const r = await this.fetchFn(d.jwks_uri);
      if (!r.ok) throw new Error(`The identity provider's keys did not load (${r.status})`);
      for (const k of ((await r.json()) as any).keys ?? []) if (k.use !== 'enc') this.jwks.set(k.kid ?? '', createPublicKey({ key: k, format: 'jwk' }));
    }
    const k = pick();
    if (!k) throw new Error('The ID token names a key the identity provider does not publish');
    return k;
  }

  async logoutUrl(idToken: string | undefined, after: string): Promise<string | undefined> {
    const d = await this.discover().catch(() => undefined);
    if (!d?.end_session_endpoint) return undefined;
    const u = new URL(d.end_session_endpoint);
    // A sealed session carries no ID token (it would not fit a cookie): the client id names the studio instead.
    if (idToken) u.searchParams.set('id_token_hint', idToken);
    else u.searchParams.set('client_id', this.cfg.clientId);
    u.searchParams.set('post_logout_redirect_uri', after);
    return u.toString();
  }
}

// --------------------------------------------------------- the development provider

/**
 * A minimal OpenID provider for local work and tests (TH_AUTH_DEV=1): discovery, a sign-in page that
 * lists the studio's users, a token endpoint that checks PKCE, and its keys. Its RSA key lives in
 * memory for the process. It never runs when NODE_ENV is production (authConfigFromEnv refuses).
 */
export class DevProvider {
  private keys = generateKeyPairSync('rsa', { modulusLength: 2048 });
  private kid = b64u(randomBytes(8));
  private codes = new Map<string, { sub: string; groups?: string[]; clientId: string; redirectUri: string; challenge: string; nonce: string; expires: number }>();
  /** Device sign-ins of the command line (RFC 8628), by device code. */
  private devices = new Map<string, { userCode: string; clientId: string; expires: number; sub?: string; groups?: string[] }>();

  constructor(private issuer: string, private userIds: () => string[]) {}

  /** Answer a request under /auth/dev, or undefined when the path is not the provider's. */
  handle(method: string, url: URL, form: URLSearchParams | undefined): { status: number; headers: Record<string, string>; body: string } | undefined {
    const p = url.pathname.replace(/^\/auth\/dev/, '');
    const json = (status: number, b: any) => ({ status, headers: { 'content-type': 'application/json', 'cache-control': 'no-store' }, body: JSON.stringify(b) });
    if (method === 'GET' && p === '/.well-known/openid-configuration') {
      return json(200, { issuer: this.issuer, authorization_endpoint: `${this.issuer}/authorize`, token_endpoint: `${this.issuer}/token`, jwks_uri: `${this.issuer}/jwks`, device_authorization_endpoint: `${this.issuer}/device_authorization`, response_types_supported: ['code'], code_challenge_methods_supported: ['S256'], id_token_signing_alg_values_supported: ['RS256'], subject_types_supported: ['public'] });
    }
    if (method === 'GET' && p === '/jwks') return json(200, { keys: [{ ...this.keys.publicKey.export({ format: 'jwk' }), kid: this.kid, use: 'sig', alg: 'RS256' }] });
    if (p === '/authorize') {
      const q = method === 'POST' && form ? form : url.searchParams;
      const need = ['client_id', 'redirect_uri', 'state', 'nonce', 'code_challenge'];
      if (need.some((k) => !q.get(k)) || q.get('code_challenge_method') !== 'S256' || q.get('response_type') !== 'code') return json(400, { error: 'invalid_request', error_description: `Send ${need.join(', ')}, response_type=code and code_challenge_method=S256` });
      // The code goes back only to this studio's own callback: the provider is no open redirect.
      let back: URL | undefined;
      try {
        back = new URL(q.get('redirect_uri')!);
      } catch {
        back = undefined;
      }
      if (!back || back.origin !== new URL(this.issuer).origin || back.pathname !== '/auth/callback') return json(400, { error: 'invalid_request', error_description: 'redirect_uri is not this studio\'s callback' });
      if (method === 'GET') return { status: 200, headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' }, body: this.page(q) };
      const sub = form?.get('user') ?? '';
      if (!/^[a-z][a-z0-9.-]*$/.test(sub)) return json(400, { error: 'invalid_request', error_description: 'Choose a user' });
      const code = b64u(randomBytes(24));
      const groups = form?.get('groups') ? form.get('groups')!.split(',').map((g) => g.trim()).filter(Boolean) : undefined;
      this.codes.set(code, { sub, groups, clientId: q.get('client_id')!, redirectUri: q.get('redirect_uri')!, challenge: q.get('code_challenge')!, nonce: q.get('nonce')!, expires: Date.now() + 60_000 });
      back = new URL(back.toString());
      back.searchParams.set('code', code);
      back.searchParams.set('state', q.get('state')!);
      return { status: 303, headers: { location: back.toString() }, body: '' };
    }
    if (method === 'POST' && p === '/device_authorization') {
      if (!form?.get('client_id')) return json(400, { error: 'invalid_request', error_description: 'Send client_id' });
      const deviceCode = b64u(randomBytes(24));
      const letters = 'BCDFGHJKLMNPQRSTVWXZ';
      const userCode = [...randomBytes(8)].map((b, i) => (i === 4 ? '-' : '') + letters[b % letters.length]).join('');
      this.devices.set(deviceCode, { userCode, clientId: form.get('client_id')!, expires: Date.now() + 600_000 });
      return json(200, { device_code: deviceCode, user_code: userCode, verification_uri: `${this.issuer}/device`, verification_uri_complete: `${this.issuer}/device?user_code=${userCode}`, expires_in: 600, interval: 1 });
    }
    if (p === '/device') {
      if (method === 'GET') return { status: 200, headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' }, body: this.devicePage(url.searchParams.get('user_code') ?? '') };
      const userCode = (form?.get('user_code') ?? '').toUpperCase().trim();
      const sub = form?.get('user') ?? '';
      const d = [...this.devices.values()].find((x) => x.userCode === userCode && x.expires >= Date.now() && !x.sub);
      if (!d || !/^[a-z][a-z0-9.-]*$/.test(sub)) return json(400, { error: 'invalid_request', error_description: 'Enter the code the command line shows, and choose a user' });
      d.sub = sub;
      d.groups = form?.get('groups') ? form.get('groups')!.split(',').map((g) => g.trim()).filter(Boolean) : undefined;
      return { status: 200, headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' }, body: '<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Signed in</title></head><body><p>Signed in. Go back to the command line.</p></body></html>' };
    }
    if (method === 'POST' && p === '/token' && form?.get('grant_type') === 'urn:ietf:params:oauth:grant-type:device_code') {
      const deviceCode = form.get('device_code') ?? '';
      const d = this.devices.get(deviceCode);
      if (!d || d.clientId !== form.get('client_id')) return json(400, { error: 'invalid_grant' });
      if (d.expires < Date.now()) {
        this.devices.delete(deviceCode);
        return json(400, { error: 'expired_token' });
      }
      if (!d.sub) return json(400, { error: 'authorization_pending' });
      this.devices.delete(deviceCode);
      const now = Math.floor(Date.now() / 1000);
      return json(200, { token_type: 'Bearer', expires_in: 300, access_token: b64u(randomBytes(24)), id_token: this.jwt({ iss: this.issuer, aud: d.clientId, sub: d.sub, iat: now, exp: now + 300, preferred_username: d.sub, ...(d.groups ? { groups: d.groups } : {}) }) });
    }
    if (method === 'POST' && p === '/token') {
      const code = form?.get('code') ?? '';
      const c = this.codes.get(code);
      this.codes.delete(code);
      const verifier = form?.get('code_verifier') ?? '';
      if (!c || c.expires < Date.now() || c.clientId !== form?.get('client_id') || c.redirectUri !== form?.get('redirect_uri') || b64u(createHash('sha256').update(verifier).digest()) !== c.challenge) return json(400, { error: 'invalid_grant' });
      const now = Math.floor(Date.now() / 1000);
      return json(200, { token_type: 'Bearer', expires_in: 300, access_token: b64u(randomBytes(24)), id_token: this.jwt({ iss: this.issuer, aud: c.clientId, sub: c.sub, nonce: c.nonce, iat: now, exp: now + 300, preferred_username: c.sub, ...(c.groups ? { groups: c.groups } : {}) }) });
    }
    return undefined;
  }

  /** For tests: a token this provider signs, with any claims. */
  sign(claims: Record<string, any>) {
    return this.jwt(claims);
  }

  private jwt(claims: any) {
    const head = b64u(Buffer.from(JSON.stringify({ alg: 'RS256', typ: 'JWT', kid: this.kid })));
    const body = b64u(Buffer.from(JSON.stringify(claims)));
    const s = createSign('RSA-SHA256').update(`${head}.${body}`).sign(this.keys.privateKey);
    return `${head}.${body}.${b64u(s)}`;
  }

  private devicePage(userCode: string) {
    const esc = (s: string) => s.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);
    const buttons = this.userIds().map((u) => `<button type="submit" name="user" value="${esc(u)}">${esc(u)}</button>`).join('\n');
    return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Development sign-in: command line</title></head>
<body><h1>Sign the command line in</h1><p>TH_AUTH_DEV=1: a built-in provider for local work and tests. Never used in production.</p>
<form method="post" action="${esc(this.issuer)}/device"><label>Code <input name="user_code" value="${esc(userCode)}"></label>
${buttons}
</form></body></html>`;
  }

  private page(q: URLSearchParams) {
    const esc = (s: string) => s.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);
    const hidden = [...q].filter(([k]) => k !== 'user').map(([k, v]) => `<input type="hidden" name="${esc(k)}" value="${esc(v)}">`).join('');
    const buttons = this.userIds().map((u) => `<button type="submit" name="user" value="${esc(u)}">${esc(u)}</button>`).join('\n');
    return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Development sign-in</title>
<style>body{font-family:system-ui,sans-serif;max-width:32rem;margin:2rem auto;padding:0 16px;background:#fff;color:#111}button{display:block;width:100%;margin:.4rem 0;padding:.6rem;font:inherit;text-align:left;cursor:pointer}p{color:#555}</style></head>
<body><h1>Development sign-in</h1><p>TH_AUTH_DEV=1: a built-in provider for local work and tests. Never used in production. Choose the user to sign in as.</p>
<form method="post" action="${esc(this.issuer)}/authorize">${hidden}
${buttons}
</form></body></html>`;
  }
}
