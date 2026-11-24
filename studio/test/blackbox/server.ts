import { spawn, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { BRANCH, TEST_TOKEN, forkRepo, seeded } from '../support/content.js';
import type { FakeGitHub } from '../support/fake-github.js';

// Black-box harness: the studio as a separate process, reached only over HTTP. Its content branch is a
// repository of the fake GitHub, which runs in the test's own process, seeded from
// content-configurations/ (R-11-21). The studio keeps nothing on disk: a test that restarts it on the
// same repository finds its drafts on their branches.

export const STUDIO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

export interface Server {
  url: string;
  /** The fake GitHub repository the studio reads and writes, `owner/repo`. */
  repo: string;
  fake: FakeGitHub;
  child: ChildProcess;
  stop(): void;
  /** A file on the content branch, written to a temporary file so a command can read it (a sample for `deploy`). */
  contentFile(rel: string): string;
}

/** Model credentials a test server never gets unless asked for: a test must not spend on a real key. */
const AI_KEYS = ['ANTHROPIC_API_KEY', 'TH_ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_PROFILE'];

export async function startServer(extraEnv: Record<string, string> = {}, opts: { keepAiKeys?: boolean; repo?: string } = {}): Promise<Server> {
  const { fake } = await seeded();
  const repo = opts.repo ?? forkRepo();
  const port = 5100 + Math.floor(Math.random() * 800);
  const content = { TH_GITHUB_API: fake.url, TH_CONTENT_REPO: repo, TH_CONTENT_BRANCH: BRANCH, TH_GITHUB_TOKEN: TEST_TOKEN };
  const url = `http://127.0.0.1:${port}`;
  // Sign-in through the built-in development provider (R-11-32), at the address the tests use.
  const auth = { TH_AUTH_DEV: '1', TH_PUBLIC_URL: url, NODE_ENV: 'test' };
  const child = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), ['src/server/main.ts'], { cwd: STUDIO, env: { ...process.env, ...(opts.keepAiKeys ? {} : Object.fromEntries(AI_KEYS.map((k) => [k, '']))), PORT: String(port), ...content, ...auth, ...extraEnv }, stdio: ['ignore', 'pipe', 'pipe'] });
  // The server's output is read, so a full pipe never blocks it; TH_BB_SERVER_LOG keeps it in a file.
  const sink = process.env.TH_BB_SERVER_LOG ? fs.createWriteStream(process.env.TH_BB_SERVER_LOG, { flags: 'a' }) : undefined;
  child.stdout?.on('data', (d) => sink?.write(d));
  child.stderr?.on('data', (d) => sink?.write(d));
  child.on('exit', (code, signal) => sink?.write(`\n[server exited ${code ?? ''} ${signal ?? ''}]\n`));
  for (let i = 0; i < 300; i++) {
    try {
      if ((await fetch(`${url}/health/live`)).ok) break;
    } catch {
      // not up yet
    }
    if (child.exitCode !== null) throw new Error(`The studio exited with ${child.exitCode}`);
    await new Promise((r) => setTimeout(r, 100));
  }
  const contentFile = (rel: string) => {
    const text = fake.files(repo, BRANCH)[`content-configurations/${rel}`];
    if (text === undefined) throw new Error(`No content-configurations/${rel} on ${repo}`);
    const f = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'th-bb-file-')), path.basename(rel));
    fs.writeFileSync(f, text);
    return f;
  };
  return { url, repo, fake, child, stop: () => child.kill(), contentFile };
}

/** The user a call acts as when the test names none: the maker of the tenant the call names. */
const DEFAULT_USER: Record<string, string> = { owner: 'shared.designer', 'example-bank': 'example.maker', 'second-bank': 'second.maker' };

export interface Signed { cookie: string; csrf: string; me: any }

/**
 * Sign in as a user through the development provider, as a browser does: /auth/login, the provider's
 * sign-in page, the callback with the code, and /auth/me for the CSRF token (R-11-32, R-11-33).
 */
export async function signIn(base: string, user: string): Promise<Signed> {
  const jar = new Map<string, string>();
  const take = (r: Response) => {
    for (const c of r.headers.getSetCookie()) {
      const [kv] = c.split(';');
      const i = kv.indexOf('=');
      jar.set(kv.slice(0, i), kv.slice(i + 1));
    }
  };
  const cookie = () => [...jar].filter(([, v]) => v).map(([k, v]) => `${k}=${v}`).join('; ');
  const login = await fetch(`${base}/auth/login`, { redirect: 'manual' });
  take(login);
  const location = login.headers.get('location');
  // A refused sign-in (a rate limit, R-11-41) has no location: say what the studio answered rather than "Invalid URL".
  if (!location) throw new Error(`The studio did not start a sign-in for ${user}: ${login.status} ${await login.text()}`);
  const authorize = new URL(location);
  const form = new URLSearchParams(authorize.searchParams);
  form.set('user', user);
  const code = await fetch(`${authorize.origin}${authorize.pathname}`, { method: 'POST', body: form, redirect: 'manual', headers: { 'content-type': 'application/x-www-form-urlencoded' } });
  const back = code.headers.get('location');
  if (!back) throw new Error(`The development provider did not sign in ${user}: ${code.status} ${await code.text()}`);
  const cb = await fetch(back, { redirect: 'manual', headers: { cookie: cookie() } });
  take(cb);
  if (cb.status !== 303) throw new Error(`The callback refused ${user}: ${cb.status} ${await cb.text()}`);
  const me = await (await fetch(`${base}/auth/me`, { headers: { cookie: cookie() } })).json();
  return { cookie: cookie(), csrf: me.csrfToken, me };
}

/**
 * An API client. Each call is signed in as a user: `as` when given, else the actor, checker or maker
 * the body names (the server takes the user from the session and refuses a body that names another),
 * else the maker of the tenant the call names. `anonymous` sends no session.
 */
export function client(base: string, opts: { as?: string; anonymous?: boolean } = {}) {
  const sessions = new Map<string, Promise<Signed>>();
  const session = (u: string) => {
    if (!sessions.has(u)) sessions.set(u, signIn(base, u));
    return sessions.get(u)!;
  };
  const userFor = (p: string, body?: any) => {
    if (opts.as) return opts.as;
    const q = new URL(p, base).searchParams;
    const named = body?.actor ?? body?.checker ?? body?.maker ?? q.get('actor');
    if (named) return named;
    const tenant = body?.tenant ?? q.get('tenant') ?? /\/api\/tenants\/([^/]+)/.exec(p)?.[1];
    return DEFAULT_USER[tenant ?? 'owner'] ?? 'shared.designer';
  };
  const call = async (method: string, p: string, body?: any) => {
    let r: Response | undefined;
    const s = opts.anonymous ? undefined : await session(userFor(p, body));
    const headers: Record<string, string> = { ...(body ? { 'content-type': 'application/json' } : {}), ...(s ? { cookie: s.cookie, 'x-csrf-token': s.csrf } : {}) };
    // A test that blocks this process (a synchronous spawn) can outlast the server's keep-alive; the
    // next request then finds its pooled connection closed before it was sent, so it is sent once more.
    for (let tries = 0; !r; tries++) {
      try {
        r = await fetch(base + p, { method, headers, body: body ? JSON.stringify(body) : undefined });
      } catch (e: any) {
        if (tries === 0 && e.cause?.code === 'UND_ERR_SOCKET') continue;
        throw new Error(`${method} ${p}: ${e.message}${e.cause ? ` (${e.cause.code ?? ''} ${e.cause.message ?? e.cause})` : ''}`);
      }
    }
    const type = r.headers.get('content-type') ?? '';
    const data = type.includes('json') ? await r.json() : Buffer.from(await r.arrayBuffer());
    return { status: r.status, data: data as any };
  };
  return {
    get: (p: string) => call('GET', p),
    post: (p: string, body?: any) => call('POST', p, body ?? {}),
    put: (p: string, body?: any) => call('PUT', p, body ?? {}),
    del: (p: string) => call('DELETE', p),
    /** The headers of a user's session, for a raw fetch. */
    headers: async (user = 'shared.designer') => {
      const s = await session(user);
      return { cookie: s.cookie, 'x-csrf-token': s.csrf };
    },
  };
}

/** Sign a Playwright page in as a user through the development provider's sign-in page. */
export async function signInPage(page: any, base: string, user: string, returnTo = '') {
  await page.goto(`${base}/auth/login${returnTo ? `?returnTo=${encodeURIComponent(returnTo)}` : ''}`);
  await page.getByRole('button', { name: user, exact: true }).click();
  await page.waitForURL((u: URL) => u.pathname === '/' && !u.searchParams.has('code'));
  // The app has asked who is signed in and landed the user: a later navigation is not raced by it.
  await page.getByLabel('Signed in as').waitFor();
}
