import * as http from 'node:http';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { randomBytes, timingSafeEqual } from 'node:crypto';
import type { StudioEnv } from '../env.js';
import { dispatch } from './api.js';
import { DevicePending, DevProvider, OidcClient, Sessions, type AuthConfig, type Session } from './auth.js';
import { userById, users, withAssertedRoles } from '../governance/governance.js';
import { effectiveRoles, landingOf, surfacesOf } from '../governance/permissions.js';
import { attrs, contextFrom, inSpan, log, meters, SpanKind } from '../telemetry/telemetry.js';
import { RateLimiter, securityHeaders, appCsp, DEV_PAGE_CSP, BodyTooLarge, readLimited, safeFilename, maxBodyBytes } from './hardening.js';

// The studio's HTTP server: sign-in under /auth (R-11-32), the API under /api, the built web app
// from web/dist. Every API call takes its user from the session; a state-changing call from a browser
// needs the session's CSRF token in `x-csrf-token` and, when the browser sends one, an Origin of this
// server (R-11-33). The command line signs in by device authorization and sends its own sealed token
// (R-11-46); the permission table decides its calls as it does the web app's.

const TYPES: Record<string, string> = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.json': 'application/json', '.ico': 'image/x-icon' };
const SAFE = new Set(['GET', 'HEAD', 'OPTIONS']);

/** The route of a request without its ids: /api/<first segment>, so a metric's dimensions stay few. */
function routeOf(p: string): string {
  const parts = p.split('/').filter(Boolean);
  return parts[0] === 'api' ? `/api/${parts[1] ?? ''}` : p.startsWith('/health') ? p : p.startsWith('/auth/') ? `/auth/${parts[1] ?? ''}` : '/';
}

/** The user a session's identity maps to: the user whose `subject` is the identity, else whose id is (R-11-32). */
export function userFor(env: StudioEnv, subject: string): string | undefined {
  const list = users(env);
  return (list.find((u) => u.subject === subject) ?? list.find((u) => !u.subject && u.id === subject))?.id;
}

function same(a: string, b: string) {
  const x = Buffer.from(a);
  const y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
}

/** The calls that run tests, the kit, an import, an export or the assistant: limited per user (R-11-41). */
export function isExpensive(method: string, p: string): boolean {
  if (method === 'GET') return p === '/api/export/download' || p === '/api/packs/download' || p === '/api/conformance/report' || p.endsWith('/release-gate');
  return /^\/api\/(tests\/|bench\/|debug\/|kit\/|import\/|ai\/|export|packs\/build|gates|supersessions\/(test|evidence)|tenants\/[^/]+\/compatibility|git\/reload|distributions|configuration-releases)/.test(p);
}

/** `devProvider`: for tests of several instances, the one development provider they share, as they would share an identity provider. */
export function createStudioServer(env: StudioEnv, cfg: AuthConfig, opts: { devProvider?: DevProvider } = {}) {
  const sessions = new Sessions(cfg);
  const dev = cfg.dev ? opts.devProvider ?? new DevProvider(cfg.issuer, () => users(env).map((u) => u.subject ?? u.id)) : undefined;
  // The development provider is reached in process, not over the network.
  const devFetch: typeof fetch = async (input, init) => {
    const u = new URL(String(input));
    const out = dev!.handle(init?.method ?? 'GET', u, init?.body ? new URLSearchParams(String(init.body)) : undefined);
    if (!out) return new Response('{}', { status: 404 });
    return new Response(out.body || null, { status: out.status, headers: out.headers });
  };
  const oidc = new OidcClient(cfg, dev ? devFetch : fetch);
  const webRoot = path.join(env.studioRoot, 'web', 'dist');
  const publicOrigin = new URL(cfg.redirectUri!).origin;
  // Rate limits (R-11-41): sign-in per client address; the calls that run tests, kits, imports, exports or the assistant per user.
  const limits = {
    auth: new RateLimiter(Number(process.env.TH_RATE_AUTH_PER_MINUTE ?? 120)),
    expensive: new RateLimiter(Number(process.env.TH_RATE_EXPENSIVE_PER_MINUTE ?? 120)),
  };
  const bodyLimit = maxBodyBytes(process.env);
  const clientOf = (req: http.IncomingMessage) => req.socket.remoteAddress ?? 'unknown';
  /** A security event (R-11-42): a refused sign-in, CSRF or origin, a rate limit, an oversized body. No token, cookie or body is logged. */
  const security = (event: string, req: http.IncomingMessage, detail: Record<string, unknown> = {}) => {
    const values = { 'th.security.event': event, 'client.address': clientOf(req), 'http.request.method': req.method, 'url.path': (req.url ?? '').split('?')[0], ...detail };
    log('warn', 'Security event {th.security.event} from {client.address} on {url.path}', values);
    if (process.env.TH_SECURITY_LOG !== '0') console.warn(JSON.stringify({ at: new Date().toISOString(), level: 'warn', ...values }));
  };

  const send = (res: http.ServerResponse, status: number, body: any, headers: Record<string, string | string[]> = {}) => {
    res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store', ...headers }).end(JSON.stringify(body));
  };
  const readBody = (req: http.IncomingMessage) => readLimited(req, bodyLimit);
  /** The roles the identity provider asserts, from its groups, when the studio maps them (R-11-34). */
  const assertedRoles = (s: Session): string[] | undefined => (s.groups ? s.groups.filter((g) => g.startsWith(cfg.groupPrefix)).map((g) => g.slice(cfg.groupPrefix.length)) : undefined);
  const me = (s: Session | undefined) => {
    if (!s) return { signedIn: false, provider: cfg.dev ? 'development' : 'oidc' };
    const id = userFor(env, s.subject);
    const u = id ? withAssertedRoles(id, assertedRoles(s), () => userById(env, id)) : undefined;
    const roles = effectiveRoles(u?.roles);
    return { signedIn: true, provider: cfg.dev ? 'development' : 'oidc', identity: s.subject, user: u?.id, tenant: u?.tenant, roles, surfaces: u ? surfacesOf(roles) : [], landing: u ? landingOf(roles) : undefined, csrfToken: s.csrf, heartbeatSeconds: Math.round((cfg.heartbeatMinutes ?? 5) * 60) };
  };

  return http.createServer((req, res) => {
    // Security headers on every answer (R-11-43); the web app's own page gets its content security policy below.
    for (const [k, v] of Object.entries(securityHeaders(cfg.secureCookies))) res.setHeader(k, v);
    let url: URL;
    try {
      url = new URL(req.url ?? '/', publicOrigin);
    } catch {
      return send(res, 400, { error: 'Not a request path' });
    }
    // Liveness and readiness (R-10-09), with no sign-in: the process answers; it has read the content branch.
    if (url.pathname === '/health/live') return send(res, 200, { status: 'live' });
    if (url.pathname === '/health/ready') {
      const c = env.store.content;
      const ready = !!c && !!c.head;
      return send(res, ready ? 200 : 503, { status: ready ? 'ready' : 'not ready', content: ready ? c!.head.slice(0, 12) : null });
    }
    // A request is a span, its route a dimension (R-10-07).
    const route = routeOf(url.pathname);
    const t0 = performance.now();
    res.on('finish', () => meters().httpServer.record((performance.now() - t0) / 1000, attrs({ 'http.request.method': req.method, 'http.route': route, 'http.response.status_code': res.statusCode })));
    void inSpan(`${req.method} ${route}`, attrs({ 'http.request.method': req.method, 'http.route': route, 'url.path': url.pathname }), async (span) => {
      await handle(req, res, url);
      span.setAttribute('http.response.status_code', res.statusCode);
    }, { kind: SpanKind.SERVER, parent: contextFrom(req.headers) }).catch((e) => {
      console.error(`Internal error on ${req.method} ${route}:`, e?.stack ?? e);
      if (!res.headersSent) send(res, 500, { error: 'Internal error' });
    });
  });

  async function handle(req: http.IncomingMessage, res: http.ServerResponse, url: URL) {
    const method = req.method ?? 'GET';
    try {

      // Sign-in is limited per client address (R-11-41).
      if (url.pathname.startsWith('/auth/') && !limits.auth.take(clientOf(req))) {
        security('rate-limited', req, { 'th.limit': 'auth' });
        return send(res, 429, { error: 'Too many sign-in requests from this address; wait a minute', code: 'R-11-41' }, { 'retry-after': '60' });
      }

      // The development provider (TH_AUTH_DEV=1 only).
      if (url.pathname.startsWith('/auth/dev/')) {
        if (!dev) return send(res, 404, { error: 'No development provider: TH_AUTH_DEV is not set' });
        const raw = method === 'POST' ? await readBody(req) : '';
        const out = dev.handle(method, url, method === 'POST' ? new URLSearchParams(raw) : undefined);
        if (!out) return send(res, 404, { error: 'Not a path of the development provider' });
        res.writeHead(out.status, { ...(out.headers['content-type']?.startsWith('text/html') ? { 'content-security-policy': DEV_PAGE_CSP } : {}), ...out.headers }).end(out.body);
        return;
      }

      // The command line's sign-in (R-11-46): device authorization against the studio's provider, through
      // the studio, which verifies the ID token and maps it to a user as it does a browser's. Nothing is
      // held here: the device code goes back to the command line, which polls with it.
      if (url.pathname === '/auth/device' && method === 'POST') {
        try {
          const d = await oidc.deviceAuthorization();
          return send(res, 200, { device_code: d.device_code, user_code: d.user_code, verification_uri: d.verification_uri, verification_uri_complete: d.verification_uri_complete, expires_in: d.expires_in, interval: d.interval ?? 5 });
        } catch (e: any) {
          security('cli-sign-in-failed', req, { 'th.reason': String(e?.message ?? e).slice(0, 200) });
          return send(res, 502, { error: `The command line cannot sign in: ${e.message}` });
        }
      }
      if (url.pathname === '/auth/device/token' && method === 'POST') {
        let deviceCode = '';
        try {
          deviceCode = String(JSON.parse((await readBody(req)) || '{}').device_code ?? '');
        } catch {
          return send(res, 400, { error: 'Body is not JSON' });
        }
        if (!deviceCode) return send(res, 400, { error: 'Send device_code' });
        let claims: Record<string, any>;
        let refreshToken: string | undefined;
        try {
          ({ claims, refreshToken } = await oidc.redeemDevice(deviceCode));
        } catch (e: any) {
          if (e instanceof DevicePending) return send(res, e.code === 'authorization_pending' || e.code === 'slow_down' ? 428 : 401, { error: e.code });
          security('cli-sign-in-failed', req, { 'th.reason': String(e?.message ?? e).slice(0, 200) });
          return send(res, 401, { error: `Sign-in failed: ${e.message}` });
        }
        const subject = String(claims[cfg.userClaim] ?? '');
        if (!subject) return send(res, 401, { error: `The ID token has no ${cfg.userClaim} claim` });
        const groups = cfg.groupsClaim ? (Array.isArray(claims[cfg.groupsClaim]) ? claims[cfg.groupsClaim].map(String) : []) : undefined;
        const user = userFor(env, subject);
        const t = sessions.cliToken(subject, user, groups, refreshToken);
        log('info', 'Command line signed in {enduser.id}', { 'enduser.id': subject, 'th.user': user ?? '' });
        const who = me({ id: t.id, subject, user, csrf: '', expires: t.expires, groups, via: 'cli' });
        return send(res, 200, { token: t.token, expiresAt: new Date(t.expires).toISOString(), user: who.user, tenant: who.tenant, roles: who.roles, identity: subject });
      }

      if (url.pathname === '/auth/login' && method === 'GET') {
        const returnTo = (url.searchParams.get('returnTo') ?? '').startsWith('#/') ? url.searchParams.get('returnTo')! : '';
        const b = sessions.begin(returnTo);
        const location = await oidc.authorizeUrl(b);
        res.writeHead(302, { location, 'set-cookie': b.cookie, 'cache-control': 'no-store' }).end();
        return;
      }
      if (url.pathname === '/auth/callback' && method === 'GET') {
        const pending = sessions.finish(url.searchParams.get('state') ?? undefined, req.headers.cookie);
        if (!pending) {
          security('sign-in-unknown-state', req);
          return send(res, 400, { error: 'This sign-in is not one the studio started, or it has expired. Sign in again.' }, { 'set-cookie': sessions.clearLoginCookie() });
        }
        if (url.searchParams.get('error')) {
          security('sign-in-refused-by-provider', req, { 'th.oidc.error': String(url.searchParams.get('error')).slice(0, 64) });
          return send(res, 401, { error: `The identity provider refused the sign-in: ${String(url.searchParams.get('error')).slice(0, 64)}` }, { 'set-cookie': sessions.clearLoginCookie() });
        }
        let claims: Record<string, any>;
        let idToken: string;
        let refreshToken: string | undefined;
        try {
          ({ claims, idToken, refreshToken } = await oidc.redeem(url.searchParams.get('code') ?? '', pending.verifier, pending.nonce));
        } catch (e: any) {
          security('sign-in-failed', req, { 'th.reason': String(e?.message ?? e).slice(0, 200) });
          return send(res, 401, { error: `Sign-in failed: ${e.message}` }, { 'set-cookie': sessions.clearLoginCookie() });
        }
        const subject = String(claims[cfg.userClaim] ?? '');
        if (!subject) return send(res, 401, { error: `The ID token has no ${cfg.userClaim} claim` });
        const groups = cfg.groupsClaim ? (Array.isArray(claims[cfg.groupsClaim]) ? claims[cfg.groupsClaim].map(String) : []) : undefined;
        void idToken;
        const s = sessions.create(subject, userFor(env, subject), groups, refreshToken);
        log('info', 'Signed in {enduser.id}', { 'enduser.id': subject, 'th.user': userFor(env, subject) ?? '' });
        res.writeHead(303, { location: `/${pending.returnTo}`, 'set-cookie': [sessions.sessionCookie(s), sessions.clearLoginCookie()], 'cache-control': 'no-store' }).end();
        return;
      }

      // A browser's session is its sealed cookie (R-11-45); a command line's is its sealed bearer token (R-11-46).
      let cli = url.pathname.startsWith('/api/') || url.pathname === '/auth/cli/logout' ? sessions.fromCliToken(req.headers.authorization) : undefined;
      let session = cli ?? sessions.fromCookieHeader(req.headers.cookie);
      // The heartbeat (R-11-50, Issue I-139): every few minutes the session is checked with the identity provider. One the
      // provider ended stops here, and at its next heartbeat on every other instance.
      if (session && sessions.due(session)) {
        const beat = await sessions.heartbeat(session, oidc);
        if (beat === 'ended') {
          security('session-ended-by-provider', req, { 'th.user': session.user ?? '', 'th.via': session.via ?? 'browser' });
          if (!cli) res.setHeader('set-cookie', sessions.clearCookie());
          session = cli = undefined;
        } else if (beat === 'renewed') {
          if (cli) res.setHeader('x-th-renewed-token', sessions.renewedCliToken(session));
          else session.renew = true;
        }
      }
      // A used session's cookie is sent again now and then, so its idle time slides on every instance. The page's own
      // heartbeat (x-th-heartbeat) is not use: its cookie carries the new check and keeps the idle time.
      const beatOnly = req.headers['x-th-heartbeat'] === '1';
      if (session?.renew && !cli) res.setHeader('set-cookie', sessions.sessionCookie(session, beatOnly));
      if (url.pathname === '/auth/me' && method === 'GET') return send(res, session ? 200 : 401, me(session));

      if (url.pathname === '/auth/cli/logout' && method === 'POST') {
        if (!cli) return send(res, 401, { error: 'Send the command line\'s token', code: 'R-11-46' });
        sessions.destroy(cli.id, cli.expires);
        return send(res, 200, { signedOut: true });
      }

      // CSRF (R-11-33): every state-changing call from a browser carries the session's token, from this origin.
      // A command line's token comes in a header a page cannot make a browser send, so it needs none.
      if (!cli && !SAFE.has(method) && (url.pathname.startsWith('/api/') || url.pathname.startsWith('/auth/'))) {
        if (!session) return send(res, 401, { error: 'Sign in first', code: 'R-11-32' });
        const origin = req.headers.origin;
        // The studio's public origin only; the address a browser used is accepted too only with the development provider.
        if (origin && origin !== publicOrigin && !(cfg.dev && origin === `http://${req.headers.host}`)) {
          security('origin-refused', req, { 'http.request.header.origin': String(origin).slice(0, 200) });
          return send(res, 403, { error: 'A change must come from the studio itself', code: 'R-11-33' });
        }
        const token = String(req.headers['x-csrf-token'] ?? '');
        if (!token || !same(token, session.csrf)) {
          security('csrf-refused', req, { 'th.user': session.user ?? '' });
          return send(res, 403, { error: 'The CSRF token is missing or wrong; reload the studio', code: 'R-11-33' });
        }
      }

      if (url.pathname === '/auth/logout' && method === 'POST') {
        sessions.destroy(session!.id, session!.expires);
        const redirect = await oidc.logoutUrl(undefined, `${publicOrigin}/`);
        return send(res, 200, { signedOut: true, ...(redirect && !cfg.dev ? { redirect } : {}) }, { 'set-cookie': sessions.clearCookie() });
      }

      if (url.pathname.startsWith('/api/')) {
        // The studio's own sign-in only (R-11-34): no bearer token, from the runtime, a bank or anyone, is
        // accepted, but the studio's own command line token (R-11-46).
        if (req.headers.authorization && !cli) {
          security('bearer-refused', req);
          return send(res, 401, { error: 'The studio accepts no bearer token: sign in to the studio', code: 'R-11-34' });
        }
        // The calls that run something heavy are limited per user (R-11-41).
        if (session && isExpensive(method, url.pathname) && !limits.expensive.take(session.subject)) {
          security('rate-limited', req, { 'th.limit': 'expensive', 'th.user': session.user ?? '' });
          return send(res, 429, { error: 'Too many runs in a minute; wait a minute and try again', code: 'R-11-41' }, { 'retry-after': '60' });
        }
        const raw = await readBody(req);
        let body: any = undefined;
        try {
          body = raw ? JSON.parse(raw) : undefined;
        } catch {
          return send(res, 400, { error: 'Body is not JSON' });
        }
        // Before a change, read the branch again if someone else wrote to it since (R-11-21).
        if (method !== 'GET' && env.store.content && !env.store.content.busy) {
          try {
            if (await env.store.content.moved()) await env.store.reload();
          } catch {
            // GitHub cannot be reached: the change itself will say so.
          }
        }
        const user = session ? userFor(env, session.subject) : undefined;
        const out = await withAssertedRoles(user ?? '', session && assertedRoles(session), () => dispatch(env, { method, path: url.pathname, query: Object.fromEntries(url.searchParams), body, user, identity: session?.subject }));
        if (out.contentType) {
          res.writeHead(out.status, { 'content-type': out.contentType, 'content-disposition': `attachment; filename="${safeFilename(out.filename)}"` }).end(out.body);
          return;
        }
        if (out.status === 403) security('forbidden', req, { 'th.user': user ?? session?.subject ?? '', 'th.code': out.body?.code ?? '' });
        return send(res, out.status, out.body);
      }

      // Static web app, with the single-page fallback. The app itself asks /auth/me and shows sign-in.
      let file = path.join(webRoot, url.pathname === '/' ? 'index.html' : url.pathname);
      if (!file.startsWith(webRoot + path.sep) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) file = path.join(webRoot, 'index.html');
      if (!fs.existsSync(file)) {
        res.writeHead(200, { 'content-type': 'text/plain' }).end('The web app is not built. Run: npm run web:build');
        return;
      }
      if (path.extname(file) === '.html') {
        // The page names this answer's style nonce, which Fluent UI puts on the style elements it writes (ADR 0003).
        const nonce = randomBytes(18).toString('base64');
        const html = fs.readFileSync(file, 'utf8').replace(/<meta name="th-style-nonce" content="[^"]*"\s*\/?>/, `<meta name="th-style-nonce" content="${nonce}" />`);
        res.writeHead(200, { 'content-type': TYPES['.html'], 'content-security-policy': appCsp(nonce), 'cache-control': 'no-store' }).end(html);
        return;
      }
      res.writeHead(200, { 'content-type': TYPES[path.extname(file)] ?? 'application/octet-stream' });
      fs.createReadStream(file).pipe(res);
    } catch (e: any) {
      if (e instanceof BodyTooLarge) {
        security('body-too-large', req, { 'th.limit.bytes': bodyLimit });
        if (!res.headersSent) send(res, 413, { error: `The request body is larger than ${bodyLimit} bytes`, code: 'R-11-41' }, { connection: 'close' });
        return;
      }
      // An error answer gives a message and a status, never internals (secure-coding.md): the detail goes to the log.
      console.error(`Internal error on ${req.method} ${url.pathname}:`, e?.stack ?? e);
      if (!res.headersSent) send(res, 500, { error: 'Internal error' });
    }
  }
}
