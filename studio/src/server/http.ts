import * as http from 'node:http';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { timingSafeEqual } from 'node:crypto';
import type { StudioEnv } from '../env.js';
import { dispatch } from './api.js';
import { DevProvider, OidcClient, Sessions, type AuthConfig, type Session } from './auth.js';
import { userById, users, withAssertedRoles } from '../governance/governance.js';
import { effectiveRoles, landingOf, surfacesOf } from '../governance/permissions.js';
import { attrs, contextFrom, inSpan, meters, SpanKind } from '../telemetry/telemetry.js';

// The studio's HTTP server: sign-in under /auth (R-11-32), the API under /api, the built web app
// from web/dist. Every API call takes its user from the session; a state-changing call needs the
// session's CSRF token in `x-csrf-token` and, when the browser sends one, an Origin of this server
// (R-11-33).

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

export function createStudioServer(env: StudioEnv, cfg: AuthConfig) {
  const sessions = new Sessions(cfg);
  const dev = cfg.dev ? new DevProvider(cfg.issuer, () => users(env).map((u) => u.subject ?? u.id)) : undefined;
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

  const send = (res: http.ServerResponse, status: number, body: any, headers: Record<string, string | string[]> = {}) => {
    res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store', ...headers }).end(JSON.stringify(body));
  };
  const readBody = async (req: http.IncomingMessage) => {
    let raw = '';
    for await (const chunk of req) raw += chunk;
    return raw;
  };
  /** The roles the identity provider asserts, from its groups, when the studio maps them (R-11-34). */
  const assertedRoles = (s: Session): string[] | undefined => (s.groups ? s.groups.filter((g) => g.startsWith(cfg.groupPrefix)).map((g) => g.slice(cfg.groupPrefix.length)) : undefined);
  const me = (s: Session | undefined) => {
    if (!s) return { signedIn: false, provider: cfg.dev ? 'development' : 'oidc' };
    const id = userFor(env, s.subject);
    const u = id ? withAssertedRoles(id, assertedRoles(s), () => userById(env, id)) : undefined;
    const roles = effectiveRoles(u?.roles);
    return { signedIn: true, provider: cfg.dev ? 'development' : 'oidc', identity: s.subject, user: u?.id, tenant: u?.tenant, roles, surfaces: u ? surfacesOf(roles) : [], landing: u ? landingOf(roles) : undefined, csrfToken: s.csrf };
  };

  return http.createServer((req, res) => {
    const url = new URL(req.url ?? '/', publicOrigin);
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
    }, { kind: SpanKind.SERVER, parent: contextFrom(req.headers) }).catch(() => {
      if (!res.headersSent) send(res, 500, { error: 'Internal error' });
    });
  });

  async function handle(req: http.IncomingMessage, res: http.ServerResponse, url: URL) {
    const method = req.method ?? 'GET';
    try {

      // The development provider (TH_AUTH_DEV=1 only).
      if (url.pathname.startsWith('/auth/dev/')) {
        if (!dev) return send(res, 404, { error: 'No development provider: TH_AUTH_DEV is not set' });
        const raw = method === 'POST' ? await readBody(req) : '';
        const out = dev.handle(method, url, method === 'POST' ? new URLSearchParams(raw) : undefined);
        if (!out) return send(res, 404, { error: 'Not a path of the development provider' });
        res.writeHead(out.status, out.headers).end(out.body);
        return;
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
        if (!pending) return send(res, 400, { error: 'This sign-in is not one the studio started, or it has expired. Sign in again.' }, { 'set-cookie': sessions.clearLoginCookie() });
        if (url.searchParams.get('error')) return send(res, 401, { error: `The identity provider refused the sign-in: ${url.searchParams.get('error')}` }, { 'set-cookie': sessions.clearLoginCookie() });
        let claims: Record<string, any>;
        let idToken: string;
        try {
          ({ claims, idToken } = await oidc.redeem(url.searchParams.get('code') ?? '', pending.verifier, pending.nonce));
        } catch (e: any) {
          return send(res, 401, { error: `Sign-in failed: ${e.message}` }, { 'set-cookie': sessions.clearLoginCookie() });
        }
        const subject = String(claims[cfg.userClaim] ?? '');
        if (!subject) return send(res, 401, { error: `The ID token has no ${cfg.userClaim} claim` });
        const groups = cfg.groupsClaim ? (Array.isArray(claims[cfg.groupsClaim]) ? claims[cfg.groupsClaim].map(String) : []) : undefined;
        const s = sessions.create(subject, userFor(env, subject), idToken, groups);
        res.writeHead(303, { location: `/${pending.returnTo}`, 'set-cookie': [sessions.sessionCookie(s), sessions.clearLoginCookie()], 'cache-control': 'no-store' }).end();
        return;
      }

      const session = sessions.fromCookieHeader(req.headers.cookie);
      if (url.pathname === '/auth/me' && method === 'GET') return send(res, session ? 200 : 401, me(session));

      // CSRF (R-11-33): every state-changing call carries the session's token, from this origin.
      if (!SAFE.has(method) && (url.pathname.startsWith('/api/') || url.pathname.startsWith('/auth/'))) {
        if (!session) return send(res, 401, { error: 'Sign in first', code: 'R-11-32' });
        const origin = req.headers.origin;
        if (origin && origin !== publicOrigin && origin !== `http://${req.headers.host}`) return send(res, 403, { error: 'A change must come from the studio itself', code: 'R-11-33' });
        const token = String(req.headers['x-csrf-token'] ?? '');
        if (!token || !same(token, session.csrf)) return send(res, 403, { error: 'The CSRF token is missing or wrong; reload the studio', code: 'R-11-33' });
      }

      if (url.pathname === '/auth/logout' && method === 'POST') {
        sessions.destroy(session!.id);
        const redirect = await oidc.logoutUrl(session!.idToken, `${publicOrigin}/`);
        return send(res, 200, { signedOut: true, ...(redirect && !cfg.dev ? { redirect } : {}) }, { 'set-cookie': sessions.clearCookie() });
      }

      if (url.pathname.startsWith('/api/')) {
        // The studio's own sign-in only (R-11-34): no bearer token, from the runtime, a bank or anyone, is accepted.
        if (req.headers.authorization) return send(res, 401, { error: 'The studio accepts no bearer token: sign in to the studio', code: 'R-11-34' });
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
          res.writeHead(out.status, { 'content-type': out.contentType, 'content-disposition': `attachment; filename="${out.filename}"` }).end(out.body);
          return;
        }
        return send(res, out.status, out.body);
      }

      // Static web app, with the single-page fallback. The app itself asks /auth/me and shows sign-in.
      let file = path.join(webRoot, url.pathname === '/' ? 'index.html' : url.pathname);
      if (!file.startsWith(webRoot) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) file = path.join(webRoot, 'index.html');
      if (!fs.existsSync(file)) {
        res.writeHead(200, { 'content-type': 'text/plain' }).end('The web app is not built. Run: npm run web:build');
        return;
      }
      res.writeHead(200, { 'content-type': TYPES[path.extname(file)] ?? 'application/octet-stream' });
      fs.createReadStream(file).pipe(res);
    } catch (e: any) {
      if (!res.headersSent) send(res, 500, { error: String(e?.message ?? e) });
    }
  }
}
