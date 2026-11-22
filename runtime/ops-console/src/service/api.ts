import type { SignIn } from '../shell/auth';

// The service console's API (runtime/src/TransactHub.Service.Api, routes under /service/api). Every call carries the
// signed-in service engineer. A refusal carries its error code and the link to its runbook.

export class ServiceError extends Error {
  constructor(readonly status: number, message: string, readonly code?: string, readonly runbook?: string) { super(message); }
}

export interface Probe { name: string; group: string; status: 'ok' | 'warn' | 'fail'; detail: string; at: string; ms: number; code?: string; version?: string; dashboard?: string; runbook?: string; history: { at: string; status: string; ms: number }[] }
export interface Health { sampledAt?: string; status: string; components: Probe[]; dashboards: Record<string, string> }
export interface Version { tag: string; kind: string; workflow: string; release: string; target: string; releaseId: string; state: string; installable: boolean; notInstallable?: string }
export interface Release { releaseId: string; scope: string; release: string; target?: string; state: string; openPayments: number; promotable: boolean; storedBy: string; storedAt: string; activations: { environment: string; effectiveFrom: string; by: string }[]; facts: any[] }
export interface ErrorLink { code: string; title?: string; category: string; runbook: string }
export interface LogItem { source: 'fact' | 'log'; at: string; level: string; subject?: string; tenant?: string; scheme?: string; kind?: string; code?: string; actor?: string; step?: string; activity?: string; message?: string; category?: string; data?: any; errors: ErrorLink[]; payment?: string; trace?: string }
export interface ErrorEntry { code: string; scope: string; title?: string; category: string; retryable: boolean; isoReasonCode?: string; raisedBy: string[]; meaning: string; causes: string[]; runbook: { checks: string[]; safeActions: string[]; actions?: string[]; escalation: string }; actionsOffered: { id: string; title: string; risky: boolean; path: string; missing?: string }[]; dictionary: string }
export interface ActionSpec { id: string; title: string; target: string; risky: boolean; path: 'built' | 'outbox' | 'missing'; does: string; missing?: string }
export interface ActionResult { action: string; name: string; status: string; code?: string; runbook?: string; result?: any; needs?: string }
export interface Proposal { action: string; title: string; target?: string; runbook: string; runbookLink: string; why?: string; risky: boolean; path: string; inRunbook: boolean; confirm: string }
export interface Answer { answer: string; sources: string[]; cited: string[]; proposals: Proposal[]; model: string; stopReason: string; note: string }

export function serviceClient(auth: SignIn) {
  async function call<T>(method: string, url: string, body?: unknown, raw?: BodyInit): Promise<T> {
    const r = await fetch(`/service/api${url}`, {
      method,
      headers: { ...auth.headers(), ...(body !== undefined ? { 'content-type': 'application/json' } : {}) },
      body: raw ?? (body !== undefined ? JSON.stringify(body) : undefined),
    });
    const type = r.headers.get('content-type') ?? '';
    if (r.ok && type.includes('application/zip')) return (await r.blob()) as unknown as T;
    const text = await r.text();
    const data = text ? JSON.parse(text) : null;
    if (!r.ok) throw new ServiceError(r.status, data?.error ?? r.statusText, data?.code, data?.runbook);
    return data as T;
  }
  return {
    me: () => call<{ subject: string; displayName: string; roles: string[]; tenants: string[]; service: boolean }>('GET', '/me'),
    health: (refresh = false) => call<Health>('GET', `/health${refresh ? '?refresh=1' : ''}`),
    versions: () => call<Version[]>('GET', '/content/versions'),
    releases: () => call<Release[]>('GET', '/content/releases'),
    diff: (tag: string) => call<any>('GET', `/content/diff?tag=${encodeURIComponent(tag)}`),
    verify: (tag: string, reason: string) => call<any>('POST', '/content/verify', { tag, reason }),
    install: (tag: string, reason: string) => call<any>('POST', '/content/install', { tag, reason }),
    logs: (q: Record<string, string>) => call<{ count: number; items: LogItem[] }>('GET', `/logs?${new URLSearchParams(q)}`),
    trace: (id: string) => call<any>('GET', `/payments/${encodeURIComponent(id)}/trace`),
    errors: (q?: string) => call<{ code: string; scope: string; title?: string; category: string; retryable: boolean; iso?: string; link: string }[]>('GET', `/errors${q ? `?q=${encodeURIComponent(q)}` : ''}`),
    error: (code: string) => call<ErrorEntry>('GET', `/errors/${encodeURIComponent(code)}`),
    actions: () => call<ActionSpec[]>('GET', '/actions'),
    pending: () => call<any[]>('GET', '/actions/pending'),
    act: (action: string, target: string | undefined, reason: string, parameters?: Record<string, unknown>) => call<ActionResult>('POST', '/actions', { action, target, reason, parameters }),
    decide: (id: string, approve: boolean, reason: string) => call<ActionResult>('POST', `/actions/${encodeURIComponent(id)}/decision`, { approve, reason }),
    audit: () => call<any[]>('GET', '/audit'),
    backup: (reason: string) => call<Blob>('POST', '/backup', { reason }),
    verifyBackup: (file: Blob) => call<any>('POST', '/backup/verify', undefined, file),
    restore: (file: Blob, dryRun: boolean, reason: string) => call<any>('POST', `/restore?dryRun=${dryRun}&reason=${encodeURIComponent(reason)}`, undefined, file),
    assistantStatus: () => call<{ enabled: boolean; model: string }>('GET', '/assistant'),
    ask: (question: string, paymentId?: string, history?: { question: string; answer: string }[]) => call<Answer>('POST', '/assistant', { question, paymentId, history }),
  };
}
export type ServiceClient = ReturnType<typeof serviceClient>;
