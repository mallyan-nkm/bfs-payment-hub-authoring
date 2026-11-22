// What keeps personal data out of the studio's telemetry (R-10-08): the same two rules as the runtime's
// (runtime/src/TransactHub.Telemetry/Masking.cs). A key that names personal data has its value replaced; every
// string value has IBANs, emails and runs of eight or more digits masked, keeping the last four characters.

export const MASKED = '[masked]';

const IBAN = /\b[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\b/g;
const EMAIL = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g;
// A run of digits that stands alone, spaces allowed: an account, card, employee or identity number, a phone.
const LONG_DIGITS = /(?<![0-9A-Za-z])\+?[0-9][0-9 ]{6,}[0-9](?![0-9A-Za-z])/g;

const PERSONAL_WORDS = ['name', 'iban', 'account', 'acct', 'email', 'phone', 'mobile', 'msisdn', 'passport', 'emirates', 'address', 'birth', 'cif', 'nationalid', 'employee', 'person', 'party'];
const KNOWN = new Set(['db.name', 'db.namespace', 'db.system.name', 'db.operation.name', 'service.name', 'messaging.destination.name', 'rpc.method', 'rpc.service', 'code.function.name', 'exception.type', 'error.type', 'th.party.kind']);

export function isPersonalKey(key: string): boolean {
  if (KNOWN.has(key)) return false;
  const last = key.slice(key.lastIndexOf('.') + 1).replace(/[_-]/g, '').toLowerCase();
  return PERSONAL_WORDS.some((w) => last.includes(w));
}

function tail(s: string): string {
  const d = s.replace(/[^0-9A-Za-z]/g, '');
  return '****' + (d.length > 4 ? d.slice(-4) : '');
}

export function scrub(s: string): string {
  if (!s) return s;
  return s
    .replace(IBAN, (m) => tail(m))
    .replace(EMAIL, '[masked-email]')
    .replace(LONG_DIGITS, (m) => ((m.match(/[0-9]/g) ?? []).length >= 8 ? tail(m) : m));
}

export function maskValue(key: string, v: unknown): unknown {
  if (v === null || v === undefined) return v;
  if (isPersonalKey(key)) return MASKED;
  if (typeof v === 'string') return scrub(v);
  if (Array.isArray(v)) return v.map((x) => (typeof x === 'string' ? scrub(x) : x));
  return v;
}

export function maskAttributes<T extends Record<string, unknown> | undefined>(attrs: T): T {
  if (!attrs) return attrs;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(attrs)) out[k] = maskValue(k, v);
  return out as T;
}
