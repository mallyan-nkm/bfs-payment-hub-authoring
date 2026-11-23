import type { RegisterField, RegisterKind } from './api';

// The register form's logic, from the kind's declaration alone: no register is named here (R-05-62).

/** A field's value as the form edits it: text, a comma list for a list, a checkbox for a boolean. */
export function toInput(f: RegisterField, v: unknown): string | boolean {
  if (f.type === 'boolean') return v === true;
  if (f.type === 'list') return Array.isArray(v) ? v.join(', ') : v === undefined || v === null ? '' : String(v);
  return v === undefined || v === null ? '' : String(v);
}

/** The form's text back to a field's value. */
export function fromInput(f: RegisterField, v: string | boolean): unknown {
  if (f.type === 'boolean') return v === true;
  const s = String(v).trim();
  if (f.type === 'list') return s ? s.split(',').map((x) => x.trim()).filter(Boolean) : [];
  return s;
}

/**
 * What a change sends: the key, and only the fields the maker changed. A masked field the maker left as shown keeps the value the
 * register holds, which the console never sees in clear. A new record sends every field given.
 */
export function changedFields(kind: RegisterKind, before: Record<string, any> | null, edited: Record<string, string | boolean>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const f of kind.fields) {
    if (!(f.id in edited)) continue;
    const now = fromInput(f, edited[f.id]);
    const was = before ? toInput(f, before[f.id]) : undefined;
    if (before && edited[f.id] === was) continue;
    if (!before && (now === '' || (Array.isArray(now) && now.length === 0))) continue;
    out[f.id] = now;
  }
  const key = before?.[kind.key] ?? fromInput(kind.fields.find((f) => f.id === kind.key)!, edited[kind.key] ?? '');
  return Object.keys(out).length ? { [kind.key]: key, ...out } : {};
}

/** What is missing before a new record may be proposed: its required fields. */
export function missing(kind: RegisterKind, edited: Record<string, string | boolean>): string[] {
  return kind.fields.filter((f) => f.required && f.type !== 'boolean' && !String(edited[f.id] ?? '').trim()).map((f) => f.title);
}

/** A version's state in words. */
export function stateLabel(s: string): string {
  return s === 'pending' ? 'Waiting for consent' : s === 'approved' ? 'Approved' : s === 'rejected' ? 'Refused' : s;
}
