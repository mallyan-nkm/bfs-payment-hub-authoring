import { spawnSync } from 'node:child_process';
import * as path from 'node:path';
import { STUDIO } from '../test/blackbox/server.js';
import type { Director } from './record.js';

// The laptop stack (deploy/laptop) as the runtime videos use it: its URLs, the bank's development provider, the services' own
// APIs for what an operator does outside a screen, and commands run for real and shown in the recorder's terminal panel.
//
// The stack is brought up beforehand with a compose project of its own (COMPOSE_PROJECT_NAME) and the passwords and secrets in
// the environment (deploy/laptop/README.md). A secret's value is never shown: the terminal panel replaces every TH_LAPTOP_*
// value it finds with stars, and the videos show the variables' names only.

export const LAPTOP = path.resolve(STUDIO, '..', 'deploy', 'laptop');
export const ROOT = path.resolve(STUDIO, '..');
export const URLS = {
  studio: process.env.TH_DEMO_STUDIO_URL ?? 'http://localhost:4317',
  idp: 'http://localhost:8480',
  release: 'http://localhost:8481',
  service: 'http://localhost:8482',
  ops: 'http://localhost:8483',
  testbench: 'http://localhost:8484',
  files: 'http://localhost:8485',
};
export const BANK = process.env.TH_LAPTOP_BANK ?? 'adib';
export const ENVIRONMENT = 'test';

/** Whether the stack answers: the studio and the three consoles. */
export async function stackUp(): Promise<boolean> {
  for (const u of [`${URLS.studio}/`, `${URLS.service}/service/api/auth/config`, `${URLS.ops}/ops/api/auth/config`, `${URLS.testbench}/testbench/api/auth/config`]) {
    try { if ((await fetch(u)).status >= 500) return false; } catch { return false; }
  }
  return true;
}

/** Every secret value the stack was given, to hide from anything shown. */
function secrets(): string[] {
  return Object.entries(process.env).filter(([k, v]) => /^TH_LAPTOP_.*(PASSWORD|SECRET|TOKEN)/.test(k) && v && v.length > 3).map(([, v]) => v!);
}
export function mask(text: string): string {
  let t = text;
  for (const s of secrets()) t = t.split(s).join('********');
  return t;
}

/** A command run for real from the repository root; its output, masked. */
export function sh(command: string, opts: { timeout?: number } = {}): string {
  const r = spawnSync('bash', ['-c', command], { cwd: ROOT, env: process.env, encoding: 'utf8', timeout: opts.timeout ?? 600_000, maxBuffer: 64 * 1024 * 1024 });
  return mask(`${r.stdout ?? ''}${r.stderr ?? ''}`.trimEnd());
}
export const compose = (args: string) => sh(`docker compose -f deploy/laptop/compose.yaml ${args}`);

/** A token of the bank's development provider: a person (svc.engineer, bank.approver, ops.operator, ...) or a machine client. */
export async function token(who: string): Promise<string> {
  const machine: Record<string, string | undefined> = { 'th-pipeline': process.env.TH_LAPTOP_PIPELINE_SECRET, 'th-front-door': process.env.TH_LAPTOP_FRONTDOOR_SECRET, 'th-channel': process.env.TH_LAPTOP_CHANNEL_SECRET };
  const form: Record<string, string> = who in machine
    ? { grant_type: 'client_credentials', client_id: who, client_secret: machine[who] ?? '' }
    : { grant_type: 'password', username: who, password: process.env.TH_LAPTOP_USER_PASSWORD ?? '', client_id: 'transacthub-runtime-ui' };
  const r = await fetch(`${URLS.idp}/token`, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(form) });
  if (!r.ok) throw new Error(`token for ${who}: ${r.status} ${await r.text()}`);
  return (await r.json()).access_token;
}

/** A call to one of the runtime's APIs as a person or a machine client. */
export async function call(base: string, who: string, method: string, p: string, body?: unknown): Promise<{ status: number; data: any }> {
  const r = await fetch(`${base}${p}`, { method, headers: { authorization: `Bearer ${await token(who)}`, ...(body ? { 'content-type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
  const t = await r.text();
  let data: any = t;
  try { data = JSON.parse(t); } catch { /* text */ }
  return { status: r.status, data };
}

/** Point the director's studio at the stack's studio, and sign in again there. */
export function useStackStudio(d: Director) {
  (d.server as any).url = URLS.studio;
  d.user = '';
}

/** Sign in to a runtime console at the bank's development provider, as one of its synthetic users. */
export async function runtimeSignIn(d: Director, base: string, who: 'service engineer' | 'bank approver' | 'operator' | 'supervisor' | 'tester', hash = '') {
  const p = d.page;
  await p.goto(`${base}/`);
  await p.waitForLoadState('load');
  await p.locator('button').first().waitFor();
  // Signed in already as someone else: sign out first.
  const out = p.getByRole('button', { name: 'Sign out' });
  if (await out.count()) await d.click(out.first());
  await d.click(p.getByRole('button', { name: 'Sign in' }));
  await p.waitForURL((u: URL) => u.port === '8480');
  await d.wait(d.pace.after);
  await d.click(p.getByRole('button', { name: new RegExp(`Sign in as Example ${who}`) }));
  await p.waitForURL((u: URL) => u.origin === base && !u.searchParams.has('code'));
  await p.getByRole('button', { name: 'Sign out' }).waitFor();
  if (hash) await runtimeGo(d, base, hash);
}

/** Open a runtime console's page by its hash route. */
export async function runtimeGo(d: Director, base: string, hash: string) {
  await d.page.goto(`${base}/#/${hash}`);
  // The consoles poll (the read model's lag, a run's progress), so the network is never idle: wait for the page's heading.
  await d.page.locator('main h1, main h2').first().waitFor({ timeout: 15_000 }).catch(() => undefined);
  await d.page.waitForTimeout(d.dry ? 300 : 600);
  await d.wait(d.pace.after);
}
