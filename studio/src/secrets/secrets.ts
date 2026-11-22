import * as fs from 'node:fs';
import * as path from 'node:path';
import { inspect } from 'node:util';

// The one way the studio, its workers and its deploy runner read a secret (R-06-22, R-06-26): a reference, resolved here.
//   env:NAME                                 a variable the deployment sets
//   file:/absolute/path                      a mounted secret file (a Kubernetes or Docker secret, a Vault agent's sink)
//   vault:mount/path#field                   a field of a HashiCorp Vault KV version 2 secret (TH_VAULT_ADDR, its token by reference in TH_VAULT_TOKEN)
//   akv:<vault-name>/<secret-name>[#version] an Azure Key Vault secret (cloud.ts)
//   aws-sm:<region>/<secret-id>[#json-field] an AWS Secrets Manager secret, or one field of its JSON (cloud.ts)
//   aws-ssm:<region>/<parameter-name>        an AWS Systems Manager Parameter Store SecureString (cloud.ts)
// A value never appears in a message, an exception, a log line or a printed object: SecretValue prints a mark.
// A value read over the network is kept for a short time (TH_SECRET_CACHE_SECONDS, default 300) and never past it.

export const SECRET_REF = /^(env|file|vault|akv|aws-sm|aws-ssm):(\S+)$/;
/** The forms of a reference, as a message names them. */
export const REF_FORMS = 'env:NAME, file:/path, vault:mount/path#field, akv:vault/secret[#version], aws-sm:region/secret-id[#field] or aws-ssm:region/parameter';
/** Schemes read over the network: resolved by resolveSecret only, and cached for a short time. */
export const NETWORK_SCHEMES = new Set(['vault', 'akv', 'aws-sm', 'aws-ssm']);
export const DEFAULT_CACHE_SECONDS = 300;

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

export type Env = Record<string, string | undefined>;

export function parseRef(ref: string): { scheme: string; locator: string } {
  const m = SECRET_REF.exec(ref ?? '');
  if (!m) throw new SecretUnavailable(describeRef(ref), `is not a secret reference: write ${REF_FORMS}`);
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

/** env: and file: references, read now. A reference to a store over the network needs resolveSecret. */
export function resolveSecretSync(ref: string, env: Env = process.env): SecretValue {
  const { scheme, locator } = parseRef(ref);
  if (scheme === 'env') return fromEnv(ref, locator, env);
  if (scheme === 'file') return fromFile(ref, locator, env);
  throw new SecretUnavailable(ref, `is read from the ${scheme} store over the network: resolve it with resolveSecret`);
}

/** A credential a store needs to sign in (the Vault token, an Azure client secret): env: or file: only, read now. */
export function bootstrapSecret(setting: string, ref: string | undefined, env: Env): SecretValue {
  if (!ref) throw new SecretUnavailable(setting, 'is not set');
  if (!/^(env|file):/.test(ref)) throw new SecretUnavailable(setting, `must be a reference to env: or file:, not ${isSecretRef(ref) ? ref : describeRef(ref)}`);
  return resolveSecretSync(ref, env);
}

async function fromVault(ref: string, locator: string, env: Env, fetcher: typeof fetch): Promise<SecretValue> {
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

/** What a store's client is given in place of the network or the credential chain: for tests only. */
export interface SecretDeps {
  fetch?: typeof fetch;
  /** Azure Key Vault: a transport (an @azure/core-rest-pipeline HttpClient) and a credential in place of the chain. */
  azure?: { httpClient?: unknown; credential?: unknown };
  /** AWS: an endpoint (LocalStack) and credentials in place of the default chain. */
  aws?: { endpoint?: string; credentials?: { accessKeyId: string; secretAccessKey: string } | (() => Promise<{ accessKeyId: string; secretAccessKey: string }>) };
  /** The time, for the cache. */
  now?: () => number;
}

/**
 * The secret stores of one environment: the providers, their clients, and a short cache of the values read over the
 * network. A cached value is never used past its time (TH_SECRET_CACHE_SECONDS, default 300; 0 caches nothing), so a
 * rotated or revoked secret takes effect within it. When a credential read from here is refused by the system it opens,
 * withSecret reads it again from its store, once, and retries: a rotated secret then works at once.
 */
export class SecretStores {
  readonly #cache = new Map<string, { value: SecretValue; at: number }>();
  readonly #clients = new Map<string, any>();
  readonly ttlMs: number;

  constructor(readonly env: Env = process.env, readonly deps: SecretDeps = {}) {
    const s = Number(env.TH_SECRET_CACHE_SECONDS ?? DEFAULT_CACHE_SECONDS);
    this.ttlMs = Number.isFinite(s) && s > 0 ? s * 1000 : 0;
  }

  #now() {
    return (this.deps.now ?? Date.now)();
  }

  async resolve(ref: string): Promise<SecretValue> {
    const { scheme, locator } = parseRef(ref);
    if (!NETWORK_SCHEMES.has(scheme)) return resolveSecretSync(ref, this.env);
    const hit = this.#cache.get(ref);
    if (hit && this.#now() - hit.at < this.ttlMs) return hit.value;
    this.#cache.delete(ref);
    const value = await this.#read(ref, scheme, locator);
    if (this.ttlMs > 0) this.#cache.set(ref, { value, at: this.#now() });
    return value;
  }

  /** Forgets a cached value, so the next resolve reads the store again. */
  invalidate(ref: string): void {
    this.#cache.delete(ref);
  }

  /**
   * Uses a secret, and when `refused` says the system it opens refused it, reads it again from its store and uses it
   * once more. A second refusal is the caller's to report.
   */
  async withSecret<T>(ref: string, use: (v: SecretValue) => Promise<T>, refused: (e: unknown) => boolean): Promise<T> {
    try {
      return await use(await this.resolve(ref));
    } catch (e) {
      if (!refused(e)) throw e;
      this.invalidate(ref);
      return use(await this.resolve(ref));
    }
  }

  /** A client of a store, made once per key and kept. */
  client<T>(key: string, make: () => T): T {
    if (!this.#clients.has(key)) this.#clients.set(key, make());
    return this.#clients.get(key);
  }

  /** Drops a client, so the next use makes it again (a credential that could not be made). */
  forget(key: string): void {
    this.#clients.delete(key);
  }

  /** Closes the clients' connections. */
  close(): void {
    for (const c of this.#clients.values()) c?.destroy?.();
    this.#clients.clear();
    this.#cache.clear();
  }

  async #read(ref: string, scheme: string, locator: string): Promise<SecretValue> {
    if (scheme === 'vault') return fromVault(ref, locator, this.env, this.deps.fetch ?? fetch);
    const cloud = await import('./cloud.js');
    if (scheme === 'akv') return cloud.fromAzureKeyVault(this, ref, locator);
    if (scheme === 'aws-sm') return cloud.fromAwsSecretsManager(this, ref, locator);
    return cloud.fromAwsParameterStore(this, ref, locator);
  }
}

let processStores: SecretStores | undefined;
/** The stores of this process's own environment, with their cache. */
export function processSecretStores(): SecretStores {
  return (processStores ??= new SecretStores(process.env));
}

/**
 * Any reference. With the process's own environment the stores and their cache are kept; with another environment (a
 * test's, a deploy runner's target) the reference is read afresh.
 */
export async function resolveSecret(ref: string, env: Env = process.env, deps: SecretDeps | typeof fetch = {}): Promise<SecretValue> {
  const d: SecretDeps = typeof deps === 'function' ? { fetch: deps } : deps;
  if (env === process.env && Object.keys(d).length === 0) return processSecretStores().resolve(ref);
  const stores = new SecretStores(env, d);
  try {
    return await stores.resolve(ref);
  } finally {
    stores.close();
  }
}
