import type { FieldDecl, View } from './api';

// How a declared field's value is shown: by its declared format only. No scheme is named here.

/**
 * A row of the documents tab (Issue I-97): whether the file was received or sent, the leg it belongs to by the leg's title (its
 * instance when the payment has no such leg), its hash shortened, and whether the host that kept it signed it.
 */
export function documentRow(
  doc: { direction: string; leg?: string | null; contentHash: string; signed?: boolean; signingKeyId?: string | null },
  legs: { instance: string; title: string }[],
): { direction: 'received' | 'sent'; leg: string; hash: string; signature: string } {
  const received = doc.direction === 'in' || doc.direction === 'inward';
  return {
    direction: received ? 'received' : 'sent',
    leg: doc.leg ? legs.find((l) => l.instance === doc.leg)?.title ?? doc.leg : '—',
    hash: doc.contentHash.slice(0, 12),
    signature: doc.signed ? `signed by key ${doc.signingKeyId ?? '?'}` : 'not signed',
  };
}

export function show(value: unknown, format?: string): string {
  if (value === null || value === undefined || value === '') return '—';
  switch (format) {
    case 'amount': {
      const n = Number(value);
      return Number.isFinite(n) ? n.toLocaleString('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) : String(value);
    }
    case 'count':
      return String(value);
    case 'datetime': {
      const d = new Date(String(value));
      return Number.isNaN(d.getTime()) ? String(value) : d.toISOString().replace('T', ' ').replace(/\.\d+Z$/, 'Z');
    }
    case 'date':
      return String(value).slice(0, 10);
    case 'json':
      return typeof value === 'string' ? value : Array.isArray(value) && value.length === 0 ? 'none' : JSON.stringify(value);
    default:
      return typeof value === 'object' ? JSON.stringify(value) : String(value);
  }
}

/** The tone of a status-like value, for its badge. */
export function tone(value: unknown): 'pass' | 'fail' | 'warn' | 'info' {
  const v = String(value ?? '').toLowerCase();
  if (['pass', 'clear', 'ack', 'accp', 'acsc', 'settled', 'approved', 'released', 'posted', 'verified', 'done', 'accepted', 'decided', 'unique', 'reserved', 'notified', 'mapped'].includes(v)) return 'pass';
  if (['reject', 'rejected', 'nak', 'rjct', 'declined', 'hit', 'failed', 'refused', 'error', 'expired', 'unmappable'].includes(v)) return 'fail';
  if (['open', 'hold', 'repair', 'pending', 'pending-approval', 'review', 'unknown', 'unverified', 'returned'].includes(v)) return 'warn';
  return 'info';
}

/** Grouped fields of a section, in declared order; fields with no group last. */
export function grouped(fields: FieldDecl[] = [], groups: { id: string; title: string }[] = []): { title: string; fields: FieldDecl[] }[] {
  const out = groups.map((g) => ({ title: g.title, fields: fields.filter((f) => f.group === g.id) })).filter((g) => g.fields.length > 0);
  const rest = fields.filter((f) => !f.group || !groups.some((g) => g.id === f.group));
  if (rest.length) out.push({ title: 'Other', fields: rest });
  return out;
}

/** The list's declared columns for a scheme: the platform's, the scheme's and its legs', each with where it reads from. */
export function columnsFor(views: View[], scheme?: string): { key: string; label: string; format?: string; from: 'subject' | string }[] {
  const out: { key: string; label: string; format?: string; from: string }[] = [];
  for (const v of views.filter((x) => !x.scheme || x.scheme === scheme)) {
    for (const c of v.subject?.columns ?? []) {
      const f = v.subject?.fields?.find((x) => x.id === c);
      if (f && !out.some((o) => o.from === 'subject' && o.key === c)) out.push({ key: c, label: f.label, format: f.format, from: 'subject' });
    }
    if (scheme)
      for (const l of v.legs ?? [])
        for (const c of l.columns ?? []) {
          const f = l.fields?.find((x) => x.id === c);
          if (f) out.push({ key: c, label: `${f.label}`, format: f.format, from: l.leg });
        }
  }
  return out;
}

/** The declared filters for a scheme, as query keys of the API (f:<view>:<subject or leg>:<field>). */
export function filtersFor(views: View[], scheme?: string) {
  const out: { key: string; label: string; match: string; field: FieldDecl }[] = [];
  for (const v of views.filter((x) => (scheme ? x.scheme === scheme : false) || !x.scheme)) {
    const sections: [string, typeof v.subject][] = [['subject', v.subject], ...(scheme ? (v.legs ?? []).map((l) => [l.leg, l] as [string, typeof v.subject]) : [])];
    for (const [where, s] of sections)
      for (const flt of s?.filters ?? []) {
        const field = s?.fields?.find((f) => f.id === flt.field);
        if (field) out.push({ key: `${v.name}:${where}:${field.id}`, label: flt.label ?? field.label, match: flt.match ?? 'exact', field });
      }
  }
  return out;
}
