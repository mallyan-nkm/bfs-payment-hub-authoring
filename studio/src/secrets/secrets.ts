import * as fs from 'node:fs';
import * as path from 'node:path';
import { inspect } from 'node:util';

// The one way the studio, its workers and its deploy runner read a secret (R-06-22): a reference, resolved here.
//   env:NAME                 a variable the deployment sets
//   file:/absolute/path      a mounted secret file (a Kubernetes or Docker secret, a Vault agent's sink)
//   vault:mount/path#field   a field of a HashiCorp Vault KV version 2 secret (TH_VAULT_ADDR, its token by reference in TH_VAULT_TOKEN)
//   akv:...                  Azure Key Vault: named, not built (Issue I-73)
// A value never appears in a message, an exception, a log line or a printed object: SecretValue prints a mark.

export const SECRET_REF = /^(env|file|vault|akv):(\S+)$/;

export class SecretUnavailable extends Error {
  constructor(readonly reference: string, reason: string) {
    super(`Secret ${reference} ${reason}`);
    this.name = 'SecretUnavailable';
  }
}

/** A secret's value. Printed, logged or serialised by mistake, it shows `[secret]`; only reveal() gives the text. */
export class SecretValue {
  readonly #value: string;
  constructor(value: string) {
    this.#value = value;
  }
  reveal(): string {
    return this.#value;
  }
  toString() {
    return '[secret]';
  }
  toJSON() {
    return '[secret]';
  }
  [inspect.custom]() {
    return '[secret]';
  }
}

export const isSecretRef = (s: unknown): s is string => typeof s === 'string' && SECRET_REF.test(s);

/** A reference as it may be shown: the reference itself, else only the length of what was given (it may be a value). */
export const describeRef = (s: unknown) => (isSecretRef(s) ? s : `(a value of ${typeof s === 'string' ? s.length : 0} characters that is not a reference)`);

type Env = Record<string, string | undefined>;

function parse(ref: string): { scheme: string; locator: string } {
  const m = SECRET_REF.exec(ref ?? '');
  if (!m) throw new SecretUnavailable(describeRef(ref), 'is not a secret reference: write env:NAME, file:/path or vault:mount/path#field');
  return { scheme: m[1], locator: m[2] };
}

function fromEnv(ref: string, name: string, env: Env): SecretValue {
  const v = env[name];
  if (!v) throw new SecretUnavailable(ref, 'is not set');
  return new SecretValue(v);
}

function fromFile(ref: string, p: string, env: Env): SecretValue {
  if (!path.isAbsolute(p) || p.split(/[\\/]/).includes('..')) throw new SecretUnavailable(ref, "is not an absolute path without '..'");
  const full = path.resolve(p);
  const roots = (env.TH_SECRET_FILE_ROOTS ?? '').split(':').map((r) => r.trim()).filter(Boolean);
  if (roots.length && !roots.some((r) => full.startsWith(path.resolve(r).replace(/\/$/, '') + '/'))) throw new SecretUnavailable(ref, 'is outside the folders TH_SECRET_FILE_ROOTS allows');
  let text: string;
  try {
    text = fs.readFileSync(full, 'utf8');
  } catch (e: any) {
    throw new SecretUnavailable(ref, `cannot be read (${e?.code ?? 'error'})`);
  }
  text = text.replace(/\r?\n$/, '');
  if (!text) throw new SecretUnavailable(ref, 'is empty');
  return new SecretValue(text);
}

/** env: and file: references, read now. A vault: reference needs resolveSecret. */
export function resolveSecretSync(ref: string, env: Env = process.env): SecretValue {
  const { scheme, locator } = parse(ref);
  if (scheme === 'env') return fromEnv(ref, locator, env);
  if (scheme === 'file') return fromFile(ref, locator, env);
  if (scheme === 'akv') throw new SecretUnavailable(ref, 'names Azure Key Vault, whose provider is not built (Issue I-73): use env:, file: or vault:');
  throw new SecretUnavailable(ref, 'is read from Vault over the network: resolve it with resolveSecret');
}

/** Any reference, Vault's included. */
export async function resolveSecret(ref: string, env: Env = process.env, fetcher: typeof fetch = fetch): Promise<SecretValue> {
  const { scheme, locator } = parse(ref);
  if (scheme !== 'vault') return resolveSecretSync(ref, env);
  const addr = env.TH_VAULT_ADDR;
  if (!addr) throw new SecretUnavailable(ref, 'names the vault store, which this installation has not configured (TH_VAULT_ADDR)');
  const tokenRef = env.TH_VAULT_TOKEN;
  if (!tokenRef) throw new SecretUnavailable('TH_VAULT_TOKEN', 'is not set: TH_VAULT_ADDR names a Vault, and its token is read by reference (env:NAME or file:/path)');
  const hash = locator.indexOf('#');
  const slash = locator.indexOf('/');
  if (hash <= 0 || slash <= 0 || slash > hash || hash === locator.length - 1) throw new SecretUnavailable(ref, 'is not mount/path#field');
  const mount = locator.slice(0, slash);
  const p = locator.slice(slash + 1, hash);
  const field = locator.slice(hash + 1);
  if (!p || p.split('/').includes('..')) throw new SecretUnavailable(ref, "has no path, or a '..' in it");
  const url = new URL(`v1/${encodeURIComponent(mount)}/data/${p.split('/').map(encodeURIComponent).join('/')}`, addr.endsWith('/') ? addr : addr + '/');
  const headers: Record<string, string> = { 'X-Vault-Token': resolveSecretSync(tokenRef, env).reveal() };
  if (env.TH_VAULT_NAMESPACE) headers['X-Vault-Namespace'] = env.TH_VAULT_NAMESPACE;
  let res: Response;
  try {
    res = await fetcher(url, { headers });
  } catch {
    throw new SecretUnavailable(ref, 'could not be read: Vault did not answer');
  }
  if (!res.ok) throw new SecretUnavailable(ref, `could not be read: Vault answered ${res.status}`);
  let body: any;
  try {
    body = await res.json();
  } catch {
    throw new SecretUnavailable(ref, "could not be read: Vault's answer is not JSON");
  }
  const v = body?.data?.data?.[field];
  if (typeof v !== 'string' || !v) throw new SecretUnavailable(ref, `has no field ${field}`);
  return new SecretValue(v);
}
