// A thin client for the studio's HTTP API. Every call names the tenant it acts for.

export class ApiError extends Error {
  constructor(public status: number, message: string, public body: any) {
    super(message);
  }
}

let currentTenant = 'owner';
export function setApiTenant(t: string) {
  currentTenant = t;
}
export function apiTenant() {
  return currentTenant;
}

function withTenant(path: string, tenant?: string) {
  const sep = path.includes('?') ? '&' : '?';
  return `${path}${sep}tenant=${encodeURIComponent(tenant ?? currentTenant)}`;
}

async function parse(res: Response) {
  const text = await res.text();
  let body: any = text;
  try {
    body = text ? JSON.parse(text) : undefined;
  } catch {
    /* not JSON */
  }
  if (!res.ok) throw new ApiError(res.status, (body && body.error) || `${res.status} ${res.statusText}`, body);
  return body;
}

export async function get<T = any>(path: string, tenant?: string): Promise<T> {
  return parse(await fetch(withTenant(path, tenant)));
}

export async function post<T = any>(path: string, body: any = {}, tenant?: string): Promise<T> {
  const t = tenant ?? currentTenant;
  const res = await fetch(withTenant(path, t), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ tenant: t, ...body }),
  });
  return parse(res);
}

export async function put<T = any>(path: string, body: any = {}, tenant?: string): Promise<T> {
  const t = tenant ?? currentTenant;
  const res = await fetch(withTenant(path, t), { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ tenant: t, ...body }) });
  return parse(res);
}

export async function del<T = any>(path: string, tenant?: string): Promise<T> {
  return parse(await fetch(withTenant(path, tenant), { method: 'DELETE' }));
}

export function downloadUrl(path: string, tenant?: string) {
  return withTenant(path, tenant);
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

/** The roles that may author and approve in a tenant (Part 6). */
export const isDesigner = (u: User) => u.roles.some((r) => r.endsWith('designer'));
export const isChecker = (u: User) => u.roles.some((r) => r.endsWith('checker'));

/** An error message with the HTTP status in front, so a refusal reads as one. */
export function refusal(e: any): string {
  const status = e instanceof ApiError ? e.status : 0;
  const code = e instanceof ApiError && e.body?.code ? ` ${e.body.code}` : '';
  return `${status ? `Refused (${status}${code}): ` : ''}${e?.message ?? e}`;
}
