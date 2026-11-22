// Sign-in for the runtime's web apps (the operations console, and the test bench beside it): the bank's identity
// provider over OIDC, the authorisation code flow with PKCE; or, outside production, a dev sign-in with synthetic users.
// The API says which (GET /ops/api/auth/config). Nothing here keeps a password; the access token stays in this tab.

export interface AuthConfig {
  mode: 'dev' | 'oidc';
  authority?: string | null;
  clientId?: string | null;
  scopes?: string | null;
  audience?: string | null;
}

export interface SignedIn {
  subject: string;
  name: string;
}

/** The seam both apps use: who is signed in, and the headers that carry it to the API. */
export interface SignIn {
  readonly config: AuthConfig;
  current(): SignedIn | null;
  headers(): Record<string, string>;
  /** Completes a redirect back from the identity provider, if this load is one. */
  complete(): Promise<void>;
  signIn(dev?: DevIdentity): Promise<void>;
  signOut(): void;
}

export interface DevIdentity {
  sub: string;
  name: string;
  roles: string[];
}

const store = {
  get: (k: string) => { try { return sessionStorage.getItem(k); } catch { return null; } },
  set: (k: string, v: string) => { try { sessionStorage.setItem(k, v); } catch { /* storage blocked */ } },
  del: (k: string) => { try { sessionStorage.removeItem(k); } catch { /* storage blocked */ } },
};

/** base64url without padding (RFC 7636). */
export function base64url(bytes: Uint8Array): string {
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** A PKCE verifier: 32 random bytes, base64url. */
export function pkceVerifier(random: (a: Uint8Array) => Uint8Array = (a) => crypto.getRandomValues(a)): string {
  return base64url(random(new Uint8Array(32)));
}

/** The S256 challenge of a verifier. */
export async function pkceChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  return base64url(new Uint8Array(digest));
}

/** The authorisation request's URL (the code flow with PKCE). */
export function authorizeUrl(endpoint: string, p: { clientId: string; redirectUri: string; scope: string; state: string; challenge: string; audience?: string | null }): string {
  const u = new URL(endpoint);
  u.searchParams.set('response_type', 'code');
  u.searchParams.set('client_id', p.clientId);
  u.searchParams.set('redirect_uri', p.redirectUri);
  u.searchParams.set('scope', p.scope);
  u.searchParams.set('state', p.state);
  u.searchParams.set('code_challenge', p.challenge);
  u.searchParams.set('code_challenge_method', 'S256');
  if (p.audience) u.searchParams.set('audience', p.audience);
  return u.toString();
}

function claims(token: string): Record<string, any> {
  try {
    const part = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(atob(part + '='.repeat((4 - (part.length % 4)) % 4)));
  } catch {
    return {};
  }
}

class DevSignIn implements SignIn {
  constructor(readonly config: AuthConfig) {}
  current(): SignedIn | null {
    const raw = store.get('th.dev.identity');
    if (!raw) return null;
    const d = JSON.parse(raw) as DevIdentity;
    return { subject: d.sub, name: d.name };
  }
  headers(): Record<string, string> {
    const raw = store.get('th.dev.identity');
    return raw ? { 'X-TH-Dev-Identity': raw } : {};
  }
  async complete() {}
  async signIn(dev?: DevIdentity) {
    if (dev) store.set('th.dev.identity', JSON.stringify(dev));
  }
  signOut() {
    store.del('th.dev.identity');
  }
}

class OidcSignIn implements SignIn {
  constructor(readonly config: AuthConfig) {}
  private async discovery(): Promise<any> {
    const r = await fetch(`${String(this.config.authority).replace(/\/$/, '')}/.well-known/openid-configuration`);
    if (!r.ok) throw new Error(`The identity provider did not answer (${r.status})`);
    return r.json();
  }
  private redirectUri() {
    return `${location.origin}${location.pathname}`;
  }
  current(): SignedIn | null {
    const t = store.get('th.oidc.token');
    if (!t) return null;
    const c = claims(t);
    if (typeof c.exp === 'number' && c.exp * 1000 < Date.now()) return null;
    return { subject: c.sub ?? '?', name: c.name ?? c.preferred_username ?? c.sub ?? '?' };
  }
  headers(): Record<string, string> {
    const t = this.current() ? store.get('th.oidc.token') : null;
    return t ? { Authorization: `Bearer ${t}` } : {};
  }
  async complete() {
    const q = new URLSearchParams(location.search);
    const code = q.get('code');
    if (!code) return;
    if (q.get('state') !== store.get('th.oidc.state')) throw new Error('The sign-in came back with a state this tab did not send');
    const d = await this.discovery();
    const body = new URLSearchParams({ grant_type: 'authorization_code', code, redirect_uri: this.redirectUri(), client_id: String(this.config.clientId), code_verifier: store.get('th.oidc.verifier') ?? '' });
    const r = await fetch(d.token_endpoint, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body });
    if (!r.ok) throw new Error(`The identity provider refused the sign-in (${r.status})`);
    const tok = await r.json();
    store.set('th.oidc.token', tok.access_token);
    store.del('th.oidc.verifier');
    store.del('th.oidc.state');
    history.replaceState(null, '', `${location.pathname}${location.hash}`);
  }
  async signIn() {
    const d = await this.discovery();
    const verifier = pkceVerifier();
    const state = pkceVerifier();
    store.set('th.oidc.verifier', verifier);
    store.set('th.oidc.state', state);
    location.assign(authorizeUrl(d.authorization_endpoint, {
      clientId: String(this.config.clientId), redirectUri: this.redirectUri(), scope: this.config.scopes ?? 'openid profile', state,
      challenge: await pkceChallenge(verifier), audience: this.config.audience,
    }));
  }
  signOut() {
    store.del('th.oidc.token');
  }
}

export async function loadSignIn(configUrl = '/ops/api/auth/config'): Promise<SignIn> {
  const config = (await (await fetch(configUrl)).json()) as AuthConfig;
  const s = config.mode === 'dev' ? new DevSignIn(config) : new OidcSignIn(config);
  await s.complete();
  return s;
}
