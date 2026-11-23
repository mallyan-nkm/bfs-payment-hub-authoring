import type { SignIn } from '../shell/auth';
import { ApiError } from '../ops/api';

// The registers area's API (runtime/src/TransactHub.Operations.Api, RegisterEndpoints; R-05-62 to R-05-64). Bank customer data,
// shown masked as the operations views mask it. Every change is a maker's draft that a different bank approver consents to.

export interface RegisterField { id: string; title: string; type: 'text' | 'code' | 'date' | 'boolean' | 'list'; required: boolean; personal: boolean; mask?: string | null; lookup: boolean; iso?: string | null; references?: string | null }
export interface RegisterKind { name: string; title: string; description: string; key: string; titleField: string; fields: RegisterField[] }
export interface RegistersInfo { kinds: RegisterKind[]; tenants: Record<string, { maker: boolean; approver: boolean }> }
export interface Version { id: number; key: string; versionNo: number; validFrom: string; changeId: string; state: 'pending' | 'approved' | 'rejected'; madeBy: string; madeAt: string; decidedBy?: string | null; decidedAt?: string | null; effectiveFrom?: string | null; fields: Record<string, any> }
export interface Change { id: string; tenant: string; kind: string; source: string; madeBy: string; madeAt: string; remarks?: string | null; count: number; state: string; decidedBy?: string | null; mayDecide?: boolean; records: { key: string; versionNo: number; validFrom: string; fields: Record<string, any> }[] }

export function registersClient(auth: SignIn) {
  async function call<T>(method: string, url: string, body?: unknown): Promise<T> {
    const r = await fetch(`/ops/api/registers${url}`, {
      method, headers: { ...auth.headers(), ...(body ? { 'content-type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined,
    });
    const text = await r.text();
    const data = text ? JSON.parse(text) : null;
    if (!r.ok) throw new ApiError(r.status, data?.error ?? r.statusText);
    return data as T;
  }
  return {
    info: () => call<RegistersInfo>('GET', ''),
    search: (kind: string, q: Record<string, string>) => call<{ tenant: string; total: number; page: number; rows: Record<string, any>[] }>('GET', `/${kind}?${new URLSearchParams(q)}`),
    record: (kind: string, key: string, tenant?: string) => call<{ tenant: string; key: string; current: Record<string, any> | null; history: Version[] }>('GET', `/${kind}/${encodeURIComponent(key)}${tenant ? `?tenant=${encodeURIComponent(tenant)}` : ''}`),
    propose: (kind: string, tenant: string, fields: Record<string, unknown>, validFrom?: string, remarks?: string) =>
      call<Change | { changed: false }>('POST', `/${kind}/changes`, { tenant, records: [{ fields, ...(validFrom ? { validFrom } : {}) }], remarks }),
    pending: () => call<Change[]>('GET', '/changes/pending'),
    decide: (id: string, approve: boolean, remarks?: string) => call<Change>('POST', `/changes/${encodeURIComponent(id)}/decision`, { approve, remarks }),
  };
}
export type RegistersClient = ReturnType<typeof registersClient>;
