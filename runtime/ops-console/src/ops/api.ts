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

export interface WorkRow {
  item: string; itemKind: 'payment' | 'action' | 'alert'; tenant: string; scheme?: string; payment: string; what: string; amount?: number; currency?: string;
  waitingSince: string; assignee?: string | null; assigneeName?: string | null; escalated: boolean; level: number; assignedAt?: string | null;
  action?: string; requestedBy?: string;
  /** The role an escalated item went to, and whether it escalated by itself after the bank's escalateAfter (R-05-107). */
  escalatedTo?: string | null; automatic?: boolean;
}
export interface WorkQueue { filter: string; total: number; counts: Record<string, number>; rows: WorkRow[]; cursor: number }
export interface BulkResult { done: number; refused: number; results: { item?: string; payment?: string; ok: boolean; status?: number; error?: string }[] }
export interface SavedFilter { id: string; name: string; query: Record<string, string>; savedAt: string }
export interface FixedReport { view: string; release: string; scheme?: string; id: string; title: string; rows: string; document?: string; oldScreen?: string; columns: { key: string; label: string }[] }

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
    // The reconciliation break queue (R-09-92).
    breaks: (all = false) => call<import('./Breaks').Break[]>('GET', `/breaks${all ? '?all=true' : ''}`),
    resolveBreak: (id: string, resolution: string, remarks?: string) => call<any>('POST', `/breaks/${encodeURIComponent(id)}/resolution`, { resolution, remarks }),
    consentBreak: (id: string, approve: boolean, remarks?: string) => call<any>('POST', `/breaks/${encodeURIComponent(id)}/consent`, { approve, remarks }),
    reports: () => call<{ dimensions: string[]; fields: { key: string; label: string; scheme?: string }[] }>('GET', '/reports'),
    summary: (q: Record<string, string>) => call<{ groups: ReportGroup[]; payments: number }>('GET', `/reports/summary?${new URLSearchParams(q)}`),
    dashboard: (q: Record<string, string>) => call<Dashboard>('GET', `/reports/dashboard?${new URLSearchParams(q)}`),
    documentUrl: (id: string, doc: string) => `/ops/api/payments/${encodeURIComponent(id)}/documents/${encodeURIComponent(doc)}`,
    me: () => call<{ subject: string; displayName: string; tenants: Record<string, string[]> }>('GET', '/me'),
    // Work management (R-05-71): the queue, a change of one item or many, the live queue's long poll, bulk actions.
    queue: (filter: string) => call<WorkQueue>('GET', `/work/queue?filter=${encodeURIComponent(filter)}`),
    changes: (after: number, wait = 20) => call<{ cursor: number; changed: boolean }>('GET', `/work/changes?after=${after}&wait=${wait}`),
    work: (op: string, kind: string, id: string, remarks?: string, assignee?: string) => call<any>('POST', '/work', { op, kind, id, remarks, assignee }),
    workBulk: (op: string, items: { kind: string; id: string }[], remarks?: string, assignee?: string) => call<BulkResult>('POST', '/work/bulk', { op, items, remarks, assignee }),
    bulkActions: (ids: string[], action: string, remarks?: string) => call<BulkResult>('POST', '/payments/bulk-actions', { ids, action, remarks }),
    filters: () => call<SavedFilter[]>('GET', '/filters'),
    saveFilter: (name: string, query: Record<string, string>) => call<SavedFilter>('POST', '/filters', { name, query }),
    removeFilter: (id: string) => call<any>('POST', `/filters/${encodeURIComponent(id)}/remove`),
    // Unmasking on a bank approver's consent (R-05-69), and the console's changes of the system that wait for consent (R-09-77).
    unmask: (subject: string, field: string, remarks: string, line?: number) => call<any>('POST', '/unmask', { subject, field, line, remarks }),
    unmasked: (id: string) => call<{ value: any; until: string; label?: string }>('GET', `/unmask/${encodeURIComponent(id)}`),
    systemPending: () => call<any[]>('GET', '/system-actions/pending'),
    systemDecide: (id: string, approve: boolean, remarks: string) => call<any>('POST', `/system-actions/${encodeURIComponent(id)}/decision`, { approve, remarks }),
    // Turnaround (R-05-68), retention and erasure (R-05-70), fixed reports (R-05-67).
    breaches: (open = false) => call<{ total: number; rows: any[] }>('GET', `/tat/breaches${open ? '?open=true' : ''}`),
    due: () => call<{ total: number; rows: any[]; periods: Record<string, any> }>('GET', '/retention/due'),
    erase: (subject: string, remarks: string) => call<any>('POST', '/erasures', { subject, remarks }),
    legalHolds: (subject?: string) => call<any[]>('GET', `/legal-holds${subject ? `?subject=${encodeURIComponent(subject)}` : ''}`),
    placeHold: (subject: string, remarks: string) => call<any>('POST', '/legal-holds', { subject, remarks }),
    releaseHold: (id: string, remarks: string) => call<any>('POST', `/legal-holds/${encodeURIComponent(id)}/release`, { remarks }),
    fixedReports: () => call<FixedReport[]>('GET', '/reports/fixed'),
    fixedReportUrl: (view: string, id: string) => `/ops/api/reports/fixed/${encodeURIComponent(view)}/${encodeURIComponent(id)}.csv`,
    // The upload page (R-05-73, R-05-74): what may be uploaded, and the file to the File API's POST /files/ui through the console.
    upload: () => call<{ fileApi: boolean; environment: string; targets: import('./upload').UploadTarget[] }>('GET', '/upload'),
    uploadFile: async (tenant: string, message: string, file: Blob & { name?: string }) => {
      const form = new FormData();
      form.append('tenant', tenant);
      form.append('message', message);
      form.append('file', file, file.name ?? 'upload');
      const r = await fetch('/ops/api/upload', { method: 'POST', headers: { ...auth.headers() }, body: form });
      const text = await r.text();
      let body: any = null;
      try { body = text ? JSON.parse(text) : null; } catch { body = { error: text }; }
      // A refusal is an answer the page shows, with its code; only a missing sign-in or role is an error.
      if (r.status === 401 || (r.status === 403 && !body?.status)) throw new ApiError(r.status, body?.error ?? r.statusText);
      return { status: r.status, body };
    },
    // The console's own chain for its audit page (R-05-75): newest first, filtered, each fact with its hash, and whether it verifies.
    audit: (q: Record<string, string>) => call<{ rows: any[]; chain: import('../shell/audit').ChainState }>('GET', `/audit?${new URLSearchParams(q)}`),
    // The fraud alerts (R-05-90), the bank's ledger accounts for supervisors (R-05-91) and the rates' staleness (R-05-92).
    alerts: (all = false) => call<import('./oversight').AlertList>('GET', `/alerts${all ? '?state=all' : ''}`),
    alertAction: (id: string, action: 'release' | 'refuse', remarks?: string) => call<any>('POST', `/alerts/${encodeURIComponent(id)}/payment-action`, { action, remarks }),
    ledger: () => call<{ banks: import('./oversight').LedgerBank[] }>('GET', '/ledger-accounts'),
    rates: () => call<import('./oversight').Rates>('GET', '/fx-rates'),
    headers: () => auth.headers(),
  };
}
export type Client = ReturnType<typeof client>;
