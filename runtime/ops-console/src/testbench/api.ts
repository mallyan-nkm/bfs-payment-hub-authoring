import type { SignIn } from '../shell/auth';

// The test bench's API (runtime/tools/TransactHub.TestBench, routes under /testbench/api). Every call carries the one
// sign-in of the runtime's web app: the bank's token, or the dev identity outside production.

export class BenchError extends Error {
  constructor(readonly status: number, message: string) { super(message); }
}

export function benchClient(auth: SignIn) {
  async function call<T = any>(path: string, body?: unknown): Promise<T> {
    const r = await fetch(`/testbench/api${path}`, {
      method: body === undefined ? 'GET' : 'POST',
      headers: { ...auth.headers(), ...(body === undefined ? {} : { 'content-type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await r.text();
    const data = text && (r.headers.get('content-type') ?? '').includes('json') ? JSON.parse(text) : text;
    if (!r.ok) throw new BenchError(r.status, r.status === 401 ? 'Not signed in' : r.status === 403 ? (data?.error ?? 'Your roles do not allow the test bench (Tester)') : (data?.error ?? `HTTP ${r.status}`));
    return data as T;
  }
  return {
    call,
    me: () => call<{ name: string; roles: string[]; tenants: string[] }>('/me'),
    /** A run's report, opened in a new tab (the report is behind the sign-in too). */
    async openReport(id: string) {
      const html = await call<string>(`/runs/${id}/report.html`);
      window.open(URL.createObjectURL(new Blob([html], { type: 'text/html' })), '_blank', 'noopener');
    },
  };
}

export type BenchClient = ReturnType<typeof benchClient>;
