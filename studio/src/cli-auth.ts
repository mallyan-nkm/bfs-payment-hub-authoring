import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

// The command line's sign-in (R-11-46, Issue I-71). `studio login --server <url>` signs in by the device
// authorization flow (RFC 8628) against the studio's own identity provider, through the studio, which
// verifies the ID token and maps it to a user of governance/users.yaml as it does a browser's. The
// command line then calls the studio's API with that token; the permission table decides each call as
// it decides the web app's, and the acting user is the token's, never `--actor`.
//
// The token is kept for the operator in TH_STUDIO_TOKEN, or in a file readable by its owner only
// (TH_STUDIO_TOKEN_FILE, else ~/.config/transacthub/studio-cli.json). It is not the studio's store: the
// studio server writes nothing to disk (R-11-24).

export class CliAuthError extends Error {}

export interface SavedToken { server: string; token: string; expiresAt: string; user?: string; tenant?: string; roles?: string[] }

export function tokenFile(env: NodeJS.ProcessEnv = process.env): string {
  return env.TH_STUDIO_TOKEN_FILE || path.join(os.homedir(), '.config', 'transacthub', 'studio-cli.json');
}

/** The studio the command line talks to: `--server`, else TH_STUDIO_URL, else none (it works on its own). */
export function serverOf(args: string[], env: NodeJS.ProcessEnv = process.env): string | undefined {
  const i = args.indexOf('--server');
  const s = i >= 0 ? args[i + 1] : env.TH_STUDIO_URL;
  return s ? s.replace(/\/$/, '') : undefined;
}

function readSaved(env: NodeJS.ProcessEnv): SavedToken | undefined {
  try {
    return JSON.parse(fs.readFileSync(tokenFile(env), 'utf8')) as SavedToken;
  } catch {
    return undefined;
  }
}

/** The token for a studio: TH_STUDIO_TOKEN, else the saved one for that studio while it has not expired. */
export function tokenFor(server: string, env: NodeJS.ProcessEnv = process.env): string {
  if (env.TH_STUDIO_TOKEN) return env.TH_STUDIO_TOKEN;
  const t = readSaved(env);
  if (!t || t.server !== server) throw new CliAuthError(`Not signed in to ${server}: run \`npm run studio -- login --server ${server}\``);
  if (Date.parse(t.expiresAt) < Date.now()) throw new CliAuthError(`The sign-in to ${server} has expired: run \`npm run studio -- login --server ${server}\` again`);
  return t.token;
}

/** A call to the studio's API as the signed-in user. A refusal is an error with the studio's words. */
export async function api(server: string, method: string, p: string, body?: unknown, env: NodeJS.ProcessEnv = process.env): Promise<any> {
  const r = await fetch(`${server}${p}`, {
    method,
    headers: { authorization: `Bearer ${tokenFor(server, env)}`, ...(body !== undefined ? { 'content-type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  const out = await r.json().catch(() => ({}));
  if (!r.ok) throw new CliAuthError(`${r.status} ${out?.error ?? r.statusText}${out?.code ? ` (${out.code})` : ''}`);
  return out;
}

/** Sign in: start the device authorization, tell the operator where to go, and poll until it is done. */
export async function login(server: string, opts: { say?: (line: string) => void; env?: NodeJS.ProcessEnv; timeoutMs?: number } = {}): Promise<SavedToken> {
  const say = opts.say ?? ((l: string) => console.error(l));
  const env = opts.env ?? process.env;
  const start = await fetch(`${server}/auth/device`, { method: 'POST' });
  const d: any = await start.json().catch(() => ({}));
  if (!start.ok) throw new CliAuthError(`The studio did not start a sign-in: ${d?.error ?? start.status}`);
  say(`To sign in, open ${d.verification_uri} and enter the code ${d.user_code}`);
  if (d.verification_uri_complete) say(`(or open ${d.verification_uri_complete})`);
  let interval = Math.max(1, Number(d.interval ?? 5)) * 1000;
  const until = Date.now() + Math.min(Number(d.expires_in ?? 600) * 1000, opts.timeoutMs ?? Infinity);
  while (Date.now() < until) {
    await new Promise((r) => setTimeout(r, interval));
    const r = await fetch(`${server}/auth/device/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ device_code: d.device_code }) });
    const out: any = await r.json().catch(() => ({}));
    if (r.status === 428) {
      if (out.error === 'slow_down') interval += 5000;
      continue;
    }
    if (!r.ok) throw new CliAuthError(`Sign-in failed: ${out?.error ?? r.status}`);
    const saved: SavedToken = { server, token: out.token, expiresAt: out.expiresAt, user: out.user, tenant: out.tenant, roles: out.roles };
    if (!out.user) throw new CliAuthError(`${out.identity} is signed in but is not a user of the studio`);
    if (!env.TH_STUDIO_TOKEN) {
      const f = tokenFile(env);
      fs.mkdirSync(path.dirname(f), { recursive: true, mode: 0o700 });
      fs.writeFileSync(f, JSON.stringify(saved, null, 2), { mode: 0o600 });
    }
    return saved;
  }
  throw new CliAuthError('The sign-in was not finished in time; run login again');
}

/** Sign out: the studio refuses the token from now on, and the saved one is removed. */
export async function logout(server: string, env: NodeJS.ProcessEnv = process.env): Promise<void> {
  let token: string | undefined;
  try {
    token = tokenFor(server, env);
  } catch {
    token = undefined;
  }
  if (token) await fetch(`${server}/auth/cli/logout`, { method: 'POST', headers: { authorization: `Bearer ${token}` } }).catch(() => undefined);
  const saved = readSaved(env);
  if (saved && saved.server === server) fs.rmSync(tokenFile(env), { force: true });
}
