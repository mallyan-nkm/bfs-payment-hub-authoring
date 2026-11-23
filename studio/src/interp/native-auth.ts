import { Agent, fetch as undiciFetch } from 'undici';
import { resolveSecret } from '../secrets/secrets.js';

// How a worker authenticates to the runtime's native host (R-08-17, R-08-21, Issue I-121). One of, or both:
//
//   - a bearer token of the bank's identity provider:
//       TH_NATIVE_TOKEN            a reference to a token someone else keeps fresh (env:, file:, vault:, akv:, aws-sm:, aws-ssm:), or
//       TH_NATIVE_TOKEN_URL        the provider's token endpoint, with TH_NATIVE_CLIENT_ID, TH_NATIVE_CLIENT_SECRET (a reference)
//                                  and optionally TH_NATIVE_SCOPE / TH_NATIVE_AUDIENCE: the worker gets its own client-credentials
//                                  token and gets a new one before the old one expires;
//   - mutual TLS: TH_NATIVE_CLIENT_CERT and TH_NATIVE_CLIENT_KEY (references to PEM), and TH_NATIVE_CA (a reference to the PEM of
//     the CA that signs the native host's certificate). The host takes the client certificate in place of a token, with the same
//     role per tenant (`native-caller`).
//
// No value is logged or returned: a token is only ever put in the Authorization header of a call to the host.

export interface NativeAuthEnv { [k: string]: string | undefined }

let cached: { key: string; token: string; refreshAt: number } | undefined;
let inflight: Promise<string> | undefined;

/** The client-credentials token, from the cache while it has more than a minute left (or a tenth of its life). */
async function clientCredentials(env: NativeAuthEnv, fetchFn: typeof fetch): Promise<string> {
  const url = env.TH_NATIVE_TOKEN_URL!;
  const clientId = env.TH_NATIVE_CLIENT_ID;
  if (!clientId || !env.TH_NATIVE_CLIENT_SECRET) throw new Error('TH_NATIVE_TOKEN_URL needs TH_NATIVE_CLIENT_ID and TH_NATIVE_CLIENT_SECRET (a reference)');
  const key = `${url}|${clientId}|${env.TH_NATIVE_SCOPE ?? ''}|${env.TH_NATIVE_AUDIENCE ?? ''}`;
  if (cached && cached.key === key && Date.now() < cached.refreshAt) return cached.token;
  if (inflight) return inflight;
  inflight = (async () => {
    const secret = (await resolveSecret(env.TH_NATIVE_CLIENT_SECRET!)).reveal();
    const body = new URLSearchParams({ grant_type: 'client_credentials', client_id: clientId, client_secret: secret });
    if (env.TH_NATIVE_SCOPE) body.set('scope', env.TH_NATIVE_SCOPE);
    if (env.TH_NATIVE_AUDIENCE) body.set('audience', env.TH_NATIVE_AUDIENCE);
    const r = await fetchFn(url, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body, signal: AbortSignal.timeout(10_000) });
    if (!r.ok) throw new Error(`The identity provider refused the worker's client credentials (${r.status})`);
    const tok = (await r.json()) as { access_token?: string; expires_in?: number };
    if (!tok.access_token) throw new Error('The identity provider sent no access token');
    const life = Math.max(10, Number(tok.expires_in ?? 300)) * 1000;
    cached = { key, token: tok.access_token, refreshAt: Date.now() + Math.max(life / 10, life - 60_000) };
    return tok.access_token;
  })();
  try {
    return await inflight;
  } finally {
    inflight = undefined;
  }
}

/** The Authorization header for a call to the native host, or none. */
export async function nativeAuthHeaders(env: NativeAuthEnv = process.env, fetchFn: typeof fetch = fetch): Promise<Record<string, string>> {
  if (env.TH_NATIVE_TOKEN_URL) return { authorization: `Bearer ${await clientCredentials(env, fetchFn)}` };
  if (env.TH_NATIVE_TOKEN) return { authorization: `Bearer ${(await resolveSecret(env.TH_NATIVE_TOKEN)).reveal()}` };
  return {};
}

/** Forget the cached token: the host refused it (401), so the next call fetches a new one. */
export function forgetNativeToken() {
  cached = undefined;
}

let agent: { key: string; agent: Agent } | undefined;

/** The connection to the native host: mutual TLS when the client certificate is configured, else the plain fetch. */
export async function nativeFetch(url: string, init: { method: string; headers: Record<string, string>; body: string }, env: NativeAuthEnv = process.env): Promise<{ status: number; json(): Promise<any> }> {
  if (!env.TH_NATIVE_CLIENT_CERT) return fetch(url, init);
  if (!env.TH_NATIVE_CLIENT_KEY) throw new Error('TH_NATIVE_CLIENT_CERT needs TH_NATIVE_CLIENT_KEY (a reference to the PEM private key)');
  const key = `${env.TH_NATIVE_CLIENT_CERT}|${env.TH_NATIVE_CLIENT_KEY}|${env.TH_NATIVE_CA ?? ''}`;
  if (!agent || agent.key !== key) {
    const [cert, pkey, ca] = await Promise.all([
      resolveSecret(env.TH_NATIVE_CLIENT_CERT).then((s) => s.reveal()),
      resolveSecret(env.TH_NATIVE_CLIENT_KEY).then((s) => s.reveal()),
      env.TH_NATIVE_CA ? resolveSecret(env.TH_NATIVE_CA).then((s) => s.reveal()) : Promise.resolve(undefined),
    ]);
    agent = { key, agent: new Agent({ connect: { cert, key: pkey, ...(ca ? { ca } : {}), rejectUnauthorized: true } }) };
  }
  return undiciFetch(url, { ...init, dispatcher: agent.agent }) as any;
}
