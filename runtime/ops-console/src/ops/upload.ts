// The upload page's own logic (R-05-73, R-05-74, Issue I-201), kept apart from the page so the unit tests reach it.

export interface UploadMessage { id: string; title: string; scheme: string; kind: 'start' | 'deliver' }
export interface UploadTarget { tenant: string; environment: string; distribution: string | null; messages: UploadMessage[] }
export interface UploadAnswer { outcome: 'kept' | 'refused' | 'started'; text: string; code?: string; runbook?: string; payment?: string; file?: string; distribution?: string }

/** The service console's route of a code's runbook, where the console's answer gives none. */
export const runbookOf = (code: string) => `#/service/errors/${encodeURIComponent(code)}`;

/** What became of a file, from the console's answer (the File API's, with its outcome), in a person's words. */
export function uploadAnswer(body: any, httpStatus: number): UploadAnswer {
  const outcome: UploadAnswer['outcome'] = body?.outcome ?? (httpStatus >= 400 || body?.status === 'refused' ? 'refused' : body?.payment ? 'started' : 'kept');
  const code = typeof body?.code === 'string' && body.code ? body.code : undefined;
  const text = outcome === 'started' ? `Kept as ${body.file} and its payment started${body.distribution ? ` on ${body.distribution}` : ''}.`
    : outcome === 'kept' ? `Kept as ${body.file}${body.detail ? `: ${body.detail}` : '.'}`
    : `Refused${body?.file ? ` (kept as ${body.file})` : ''}: ${body?.error ?? `the File API answered ${httpStatus}`}`;
  return { outcome, text, code, runbook: body?.runbook ?? (code ? runbookOf(code) : undefined), payment: body?.payment ?? undefined, file: body?.file ?? undefined, distribution: body?.distribution ?? undefined };
}

/** The messages a person may choose, start messages first: those that begin a payment, then those delivered to a running one. */
export function messageChoices(t: UploadTarget | undefined): UploadMessage[] {
  return [...(t?.messages ?? [])].sort((a, b) => (a.kind === b.kind ? a.id.localeCompare(b.id) : a.kind === 'start' ? -1 : 1));
}
