import { createHash } from 'node:crypto';

// Canonical JSON (RFC 8785, JCS) and the content hash (R-01-12).
// JCS orders object keys by UTF-16 code units and serialises numbers as ECMAScript does,
// which is what JSON.stringify does for a finite number.

export function canonicalJson(value: unknown): string {
  if (value === null || value === undefined) return 'null';
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) throw new Error('Canonical JSON cannot hold a non-finite number');
    return JSON.stringify(value);
  }
  if (typeof value === 'boolean' || typeof value === 'string') return JSON.stringify(value);
  if (value instanceof Date) return JSON.stringify(value.toISOString());
  if (Array.isArray(value)) return '[' + value.map((v) => canonicalJson(v)).join(',') + ']';
  if (typeof value === 'object') {
    const keys = Object.keys(value as object)
      .filter((k) => (value as any)[k] !== undefined)
      .sort();
    return '{' + keys.map((k) => JSON.stringify(k) + ':' + canonicalJson((value as any)[k])).join(',') + '}';
  }
  throw new Error(`Canonical JSON cannot hold a ${typeof value}`);
}

export function contentHash(doc: unknown): string {
  return 'sha256:' + createHash('sha256').update(canonicalJson(doc), 'utf8').digest('hex');
}

/** Deep clone through JSON; documents are JSON data. */
export function clone<T>(v: T): T {
  return v === undefined ? v : JSON.parse(JSON.stringify(v));
}
