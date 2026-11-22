import { token } from './auth';

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

export async function api<T = any>(path: string, body?: unknown): Promise<T> {
  const t = token();
  const r = await fetch(`/api${path}`, {
    method: body === undefined ? 'GET' : 'POST',
    headers: { ...(t ? { authorization: `Bearer ${t}` } : {}), ...(body === undefined ? {} : { 'content-type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await r.text();
  const data = text && (r.headers.get('content-type') ?? '').includes('json') ? JSON.parse(text) : text;
  if (!r.ok) throw new ApiError(r.status, r.status === 401 ? 'Not signed in' : r.status === 403 ? (data?.error ?? 'Your roles do not allow the test bench') : (data?.error ?? `HTTP ${r.status}`));
  return data as T;
}

/** A report of a run, opened in a new tab with the bearer token (the report is behind the sign-in too). */
export async function openReport(id: string) {
  const html = await api<string>(`/runs/${id}/report.html`);
  const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }));
  window.open(url, '_blank', 'noopener');
}
