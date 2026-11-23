import type { SignIn } from '../shell/auth';

// The console's API (runtime/src/TransactHub.Operations.Api). Every call carries the signed-in operator.

export interface FieldDecl { id: string; label: string; group?: string; source: string; format?: string; mask?: string }
export interface Section {
  title?: string;
  fields?: FieldDecl[];
  groups?: { id: string; title: string }[];
  columns?: string[];
  filters?: { field: string; match?: 'exact' | 'contains' | 'range'; label?: string }[];
  lines?: { title?: string; fields: FieldDecl[]; columns?: string[] };
  documents?: { kind: string; label: string }[];
  actions?: { action: string; label?: string; role: string; secondApprover?: string; remarks?: string }[];
}
export interface LegDecl extends Section { leg: string; selectBy?: any }
export interface View { kind: string; name: string; release: string; title: string; scheme?: string; subject?: Section; legs?: LegDecl[] }

export interface Leg { id: string; instance: string; leg: string; title: string; direction: string; lastStep?: string; lastOutcome?: string; updatedAt: string; fields: Record<string, any> }
export interface PaymentRow {
  id: string; kind: string; tenant: string; scheme: string; direction: string; bulkId?: string; messageId?: string; endToEndId?: string;
  fileName?: string; amount: number; currency: string; count: number; status?: string; lastOutcome?: string; lastStep?: string;
  hold?: string; holdReason?: string; acceptedAt: string; updatedAt: string; view?: string; releaseId: string;
  fields: Record<string, any>; legs: Leg[];
}
export interface Offer { action: string; label: string; role: string; secondApprover?: string; remarksRequired: boolean; hold?: string; leg?: string }
export interface Detail {
  payment: PaymentRow;
  lines: { id: string; lineNo: number; amount: number; currency: string; fields: Record<string, any> }[];
  timeline: { seq: number; at: string; kind: string; code?: string; instance?: string; step?: string; activity?: string; actor: string; data: any; traceId?: string | null; trace?: string | null }[];
  documents: { id: string; kind: string; direction: string; fileName?: string; size: number; contentHash: string; at: string; source?: string; leg?: string | null; signed?: boolean; signingKeyId?: string | null }[];
  holds: { id: string; kind: string; activity: string; reason?: string; status: string; decision?: string; openedAt: string; decidedAt?: string; decidedBy?: string }[];
  actions: { id: string; action: string; status: string; requestedBy: string; requestedAt: string; remarks?: string; secondApprover?: string; approvedBy?: string; approvedAt?: string; result?: string }[];
  offers: Offer[];
}
export interface Facets { tenants: string[]; schemes: string[]; directions: string[]; statuses: string[]; outcomes: string[]; holdReasons: string[]; legs: string[]; legOutcomes: string[] }
export interface ReportGroup { key: string; payments: number; lines: number; amounts: { currency: string; amount: number; payments: number }[] }
export interface Dashboard {
  payments: number; lines: number; amounts: { currency: string; amount: number }[]; cut: boolean;
  byScheme: ReportGroup[]; byStatus: ReportGroup[]; byDay: ReportGroup[]; holds: ReportGroup[];
  schemes: { scheme: string; view: string; title: string; payments: number; measures: { id: string; label: string; format: string; total: number }[]; dimensions: { id: string; label: string; values: { value: string; payments: number }[] }[] }[];
}
export interface Lag { behind: number; oldestSeconds: number; through: number; applied: number; lastFactAt?: string; projectedAt?: string; rebuiltAt?: string }

export class ApiError extends Error {
  constructor(readonly status: number, message: string) { super(message); }
}

export function client(auth: SignIn) {
  async function call<T>(method: string, url: string, body?: unknown): Promise<T> {
    const r = await fetch(`/ops/api${url}`, {
      method, headers: { ...auth.headers(), ...(body ? { 'content-type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined,
    });
    const text = await r.text();
    const data = text ? JSON.parse(text) : null;
    if (!r.ok) throw new ApiError(r.status, data?.error ?? r.statusText);
    return data as T;
  }
  return {
    views: () => call<View[]>('GET', '/views'),
    facets: () => call<Facets>('GET', '/facets'),
    health: () => call<{ lag: Lag; views: string[] }>('GET', '/health'),
    payments: (q: Record<string, string>) => call<{ total: number; page: number; pageSize: number; rows: PaymentRow[] }>('GET', `/payments?${new URLSearchParams(q)}`),
    payment: (id: string) => call<Detail>('GET', `/payments/${encodeURIComponent(id)}`),
    act: (id: string, action: string, remarks?: string) => call<any>('POST', `/payments/${encodeURIComponent(id)}/actions`, { action, remarks }),
    decide: (id: string, action: string, approve: boolean, remarks?: string) => call<any>('POST', `/payments/${encodeURIComponent(id)}/actions/${action}/decision`, { approve, remarks }),
    pending: () => call<any[]>('GET', '/actions/pending'),
    reports: () => call<{ dimensions: string[]; fields: { key: string; label: string; scheme?: string }[] }>('GET', '/reports'),
    summary: (q: Record<string, string>) => call<{ groups: ReportGroup[]; payments: number }>('GET', `/reports/summary?${new URLSearchParams(q)}`),
    dashboard: (q: Record<string, string>) => call<Dashboard>('GET', `/reports/dashboard?${new URLSearchParams(q)}`),
    documentUrl: (id: string, doc: string) => `/ops/api/payments/${encodeURIComponent(id)}/documents/${encodeURIComponent(doc)}`,
    headers: () => auth.headers(),
  };
}
export type Client = ReturnType<typeof client>;
