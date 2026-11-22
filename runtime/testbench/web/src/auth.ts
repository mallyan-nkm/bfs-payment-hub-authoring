// Sign-in with the bank's OIDC identity provider: the authorization code flow with PKCE (S256), a public client. The
// test bench keeps no user and no password; it holds the bank's access token in this tab's session storage only, and
// sends it as a bearer token to the test bench API. The same client (Auth:Oidc:ClientId, by default
// transacthub-runtime-ui) is meant to serve the operations console, so one sign-in at the bank's provider covers both.

export interface AuthConfig { mode: 'oidc' | 'dev'; issuer: string; clientId: string; scope: string; audience: string }

const KEY = 'th-testbench-token';
const PENDING = 'th-testbench-pkce';

function b64url(bytes: ArrayBuffer | Uint8Array): string {
  const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let s = '';
  for (const x of b) s += String.fromCharCode(x);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function random(n = 32) {
  const a = new Uint8Array(n);
  crypto.getRandomValues(a);
  return b64url(a);
}

export async function config(): Promise<AuthConfig> {
  const r = await fetch('/auth/config');
  if (!r.ok) throw new Error('The test bench service did not give its sign-in settings');
  return r.json();
}

async function discovery(issuer: string): Promise<{ authorization_endpoint: string; token_endpoint: string }> {
  const r = await fetch(`${issuer.replace(/\/$/, '')}/.well-known/openid-configuration`);
  if (!r.ok) throw new Error(`The identity provider at ${issuer} did not answer`);
  return r.json();
}

const redirectUri = () => `${location.origin}/`;

export async function signIn(cfg: AuthConfig) {
  const d = await discovery(cfg.issuer);
  const verifier = random(48);
  const challenge = b64url(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier)));
  const state = random(16);
  sessionStorage.setItem(PENDING, JSON.stringify({ verifier, state, token: d.token_endpoint }));
  const q = new URLSearchParams({ response_type: 'code', client_id: cfg.clientId, redirect_uri: redirectUri(), scope: cfg.scope, state, code_challenge: challenge, code_challenge_method: 'S256' });
  location.assign(`${d.authorization_endpoint}?${q}`);
}

/** After the provider sends the browser back: the code for a token, checked against the state and the verifier. */
export async function completeSignIn(cfg: AuthConfig): Promise<boolean> {
  const q = new URLSearchParams(location.search);
  const code = q.get('code');
  if (!code) return false;
  const pending = JSON.parse(sessionStorage.getItem(PENDING) ?? 'null');
  sessionStorage.removeItem(PENDING);
  history.replaceState(null, '', '/');
  if (!pending || pending.state !== q.get('state')) throw new Error('The sign-in came back with a state this tab did not send');
  const r = await fetch(pending.token, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'authorization_code', code, redirect_uri: redirectUri(), client_id: cfg.clientId, code_verifier: pending.verifier }),
  });
  if (!r.ok) throw new Error('The identity provider refused the sign-in');
  const t = await r.json();
  sessionStorage.setItem(KEY, JSON.stringify({ token: t.access_token, until: Date.now() + (t.expires_in ?? 3600) * 1000 }));
  return true;
}

export function token(): string | undefined {
  const t = JSON.parse(sessionStorage.getItem(KEY) ?? 'null');
  return t && t.until > Date.now() + 10000 ? t.token : undefined;
}

export function signOut() {
  sessionStorage.removeItem(KEY);
}
