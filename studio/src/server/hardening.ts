import type * as http from 'node:http';

// The studio server's defences that are not sign-in itself (docs/north-star/security/review-2026-11.md): security
// headers and the web app's content security policy (R-11-43), a bound on a request's body and rate limits on sign-in and
// on the calls that run something heavy (R-11-41).

/**
 * The web app's page: its own scripts, styles and API only; no frame may hold it. Fluent UI writes its styles into style
 * elements at run time (ADR 0003), so the page allows a style element that carries this answer's nonce, and no other inline
 * style; the nonce is new on every answer and is never a script's.
 */
export const appCsp = (styleNonce: string) => [
  "default-src 'self'",
  "script-src 'self'",
  `style-src 'self' 'nonce-${styleNonce}'`,
  "img-src 'self' data: blob:",
  "font-src 'self'",
  "connect-src 'self'",
  "object-src 'none'",
  "base-uri 'none'",
  "form-action 'self'",
  "frame-ancestors 'none'",
].join('; ');

/** The development provider's sign-in page: an inline style and a form that posts to this server. */
export const DEV_PAGE_CSP = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

/** Headers on every answer. An API answer is data, never a page: it gets a policy that allows nothing. */
export function securityHeaders(https: boolean): Record<string, string> {
  return {
    'x-content-type-options': 'nosniff',
    'x-frame-options': 'DENY',
    'referrer-policy': 'no-referrer',
    'cross-origin-opener-policy': 'same-origin',
    'cross-origin-resource-policy': 'same-origin',
    'permissions-policy': 'camera=(), microphone=(), geolocation=(), payment=()',
    'content-security-policy': "default-src 'none'; frame-ancestors 'none'; base-uri 'none'",
    ...(https ? { 'strict-transport-security': 'max-age=31536000; includeSubDomains' } : {}),
  };
}

export class BodyTooLarge extends Error {}

/** The most bytes a request body may have: TH_MAX_BODY_MB, 32 MB by default (an import's zip, in base64, fits). */
export function maxBodyBytes(env: NodeJS.ProcessEnv = process.env): number {
  const mb = Number(env.TH_MAX_BODY_MB ?? 32);
  return Math.max(1, Number.isFinite(mb) ? mb : 32) * 1024 * 1024;
}

/** The body as text, refused past `limit` bytes before it is all read. */
export async function readLimited(req: http.IncomingMessage, limit: number): Promise<string> {
  const declared = Number(req.headers['content-length'] ?? 0);
  if (declared > limit) throw new BodyTooLarge();
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    size += (chunk as Buffer).length;
    if (size > limit) throw new BodyTooLarge();
    chunks.push(chunk as Buffer);
  }
  return Buffer.concat(chunks).toString('utf8');
}

/**
 * A fixed-window counter per key: at most `perMinute` takes in each minute. Keys are a client address or a user, never a
 * secret. The map is swept as it is used and held to `maxKeys`, so it cannot grow without bound.
 */
export class RateLimiter {
  private windows = new Map<string, { start: number; count: number }>();
  constructor(private perMinute: number, private now: () => number = Date.now, private maxKeys = 50_000) {}

  take(key: string): boolean {
    if (!(this.perMinute > 0)) return true;
    const t = this.now();
    let w = this.windows.get(key);
    if (!w || t - w.start >= 60_000) {
      if (this.windows.size >= this.maxKeys) this.sweep(t);
      w = { start: t, count: 0 };
      this.windows.set(key, w);
    }
    w.count++;
    return w.count <= this.perMinute;
  }

  private sweep(t: number) {
    for (const [k, w] of this.windows) if (t - w.start >= 60_000) this.windows.delete(k);
    // Still full: drop the oldest windows rather than grow.
    for (const k of this.windows.keys()) {
      if (this.windows.size < this.maxKeys) break;
      this.windows.delete(k);
    }
  }
}

/** A file name for Content-Disposition: letters, digits and . _ + @ - only, so a name from a query cannot add a parameter. */
export function safeFilename(name: string | undefined): string {
  return String(name ?? 'download').replace(/[^A-Za-z0-9._+@-]+/g, '-').slice(0, 180) || 'download';
}
