// A thin client for the studio's HTTP API. The server takes the user, and with it the tenant, from
// the session cookie (R-11-32); a state-changing call carries the session's CSRF token (R-11-33).

export class ApiError extends Error {
  constructor(public status: number, message: string, public body: any) {
    super(message);
  }
}

let currentTenant = 'owner';
let csrf = '';
let onSignedOut: () => void = () => {};
export function setApiTenant(t: string) {
  currentTenant = t;
}
export function apiTenant() {
  return currentTenant;
}
export function setCsrf(token: string) {
  csrf = token;
}
/** Called when a call finds the session gone (401): the app shows sign-in again. */
export function onUnauthorized(fn: () => void) {
  onSignedOut = fn;
}

async function parse(res: Response) {
  const text = await res.text();
  let body: any = text;
  try {
    body = text ? JSON.parse(text) : undefined;
  } catch {
    /* not JSON */
  }
  if (res.status === 401) onSignedOut();
  if (!res.ok) throw new ApiError(res.status, (body && body.error) || `${res.status} ${res.statusText}`, body);
  return body;
}

const send = (method: string, body?: any): RequestInit => ({
  method,
  credentials: 'same-origin',
  headers: { ...(body !== undefined ? { 'content-type': 'application/json' } : {}), 'x-csrf-token': csrf },
  body: body !== undefined ? JSON.stringify(body) : undefined,
});

// The tenant argument is kept for the views' calls; the server answers for the user's tenant only.
export async function get<T = any>(path: string, _tenant?: string): Promise<T> {
  return parse(await fetch(path, { credentials: 'same-origin' }));
}

export async function post<T = any>(path: string, body: any = {}, _tenant?: string): Promise<T> {
  return parse(await fetch(path, send('POST', body)));
}

export async function put<T = any>(path: string, body: any = {}, _tenant?: string): Promise<T> {
  return parse(await fetch(path, send('PUT', body)));
}

export async function del<T = any>(path: string, _tenant?: string): Promise<T> {
  return parse(await fetch(path, send('DELETE')));
}

export function downloadUrl(path: string, _tenant?: string) {
  return path;
}

/** Who is signed in (GET /auth/me): the user, its tenant and roles, the surfaces to show and where to land. */
export interface Me { signedIn: boolean; provider: string; identity?: string; user?: string; tenant?: string; roles: string[]; surfaces: string[]; landing?: string; csrfToken?: string }

export async function me(): Promise<Me> {
  const r = await fetch('/auth/me', { credentials: 'same-origin' });
  const body = await r.json().catch(() => ({ signedIn: false, roles: [], surfaces: [] }));
  return { roles: [], surfaces: [], ...body };
}

export async function signOut() {
  const r = await fetch('/auth/logout', send('POST', {}));
  const body = await r.json().catch(() => ({}));
  csrf = '';
  if (body.redirect) window.location.href = body.redirect;
}

export const q = (params: Record<string, string | undefined>) => {
  const sp = new URLSearchParams();
  for (const k of Object.keys(params)) if (params[k]) sp.set(k, params[k] as string);
  const s = sp.toString();
  return s ? `?${s}` : '';
};

// ------------------------------------------------------------------ shapes

export interface Issue { code: string; severity: 'error' | 'warning'; key?: string; step?: string; message: string; file?: string }
export interface Summary {
  key: string; name: string; kind: string; type: string; scope: any; release: string; title: string;
  status: string; tenant: string; provenance?: string; generated?: boolean; source?: string; issues?: number;
}
export interface Meta { tenants: string[]; owner: string; counts: Record<string, number>; checks: Record<string, string>; contextParts: string[]; roles: string[]; scripts: string[] }
export interface Check { what: string; expected: any; actual: any; passed: boolean; note?: string }
export interface TestRunReport {
  id: string; title?: string; target: string; level: string; passed: boolean; checks: Check[]; error?: string; outcome?: string;
  trace: string[]; detail?: string[]; releaseTrace: string[]; elapsed: string; warnings: string[]; record?: RunRecord;
}
export interface RunRecord {
  status: string; instances: InstanceView[]; timeline: any[]; messages: any[]; unmatched: any[]; duplicates: any[];
  bench: any[]; defaultBindings: any[]; elapsedMs: number; startedAt: string;
}
export interface InstanceView {
  id: string; kind: string; workflow: string; release: string; parent?: string; outcome?: string; reasonCode?: string; failed?: string;
  durable: boolean; trace: string[]; detail?: string[]; expandedFrom?: { release: string; hash: string }; releaseTrace: string[]; steps: any[]; deltas: any[]; ruleTraces: any[]; attempts: Record<string, number>;
  pins: Record<string, string>; bindings: any[]; supersessions: any[]; engineState?: any; context: any; startedAt: number; endedAt?: number;
}
export interface KitResult {
  id: string; construct: string; title: string; passed: boolean; expected: string[]; actual: string[];
  checks: { what: string; expected: any; actual: any; passed: boolean }[]; error?: string; notes?: string[]; elapsed?: number;
}

export interface User { id: string; tenant: string; roles: string[] }
export interface Gate { gate: string; passed: boolean; detail?: any }
export interface GateResult { gates: Gate[]; passed: boolean; firstRed?: Gate }

/** The roles that make changes and the roles that approve them as versions (R-11-29). */
export const MAKER_ROLES = ['workflow designer', 'workflow developer', 'bank configurator'];
export const APPROVER_ROLES = ['platform approver', 'bank approver'];
export const isDesigner = (u: User) => u.roles.some((r) => MAKER_ROLES.includes(r) || r.endsWith('designer'));
export const isChecker = (u: User) => u.roles.some((r) => APPROVER_ROLES.includes(r) || r.endsWith('checker'));

/** An error message with the HTTP status in front, so a refusal reads as one. */
export function refusal(e: any): string {
  const status = e instanceof ApiError ? e.status : 0;
  const code = e instanceof ApiError && e.body?.code ? ` ${e.body.code}` : '';
  return `${status ? `Refused (${status}${code}): ` : ''}${e?.message ?? e}`;
}
