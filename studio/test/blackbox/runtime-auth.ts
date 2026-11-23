import { generateKeyPairSync, sign } from 'node:crypto';
import * as os from 'node:os';

// The runtime belongs to the bank: it takes only bearer JWTs from the bank's identity provider, maps the bank's claims to
// its roles through a role map, and keeps no identities of its own (R-09-27). A black-box run plays the bank's identity
// provider: an RSA key made for the run, its JWKS given to the runtime, and one client-credentials token for the pipeline,
// whose app role the role map turns into every runtime role for every tenant. TH_TRUSTED_SIGNERS comes from the run's own
// signing key (test/support/signing-env.ts), so the runtime trusts the studio's packages and nothing else of the studio.

const ISSUER = 'https://idp.bank.example.test/realms/runtime';
const AUDIENCE = 'transacthub-runtime';
const TOKEN_VAR = 'TH_BB_RUNTIME_TOKEN';
const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const b64u = (b: Buffer | string) => Buffer.from(b).toString('base64url');

function jwt(claims: Record<string, unknown>): string {
  const now = Math.floor(Date.now() / 1000);
  const head = b64u(JSON.stringify({ alg: 'RS256', typ: 'JWT', kid: 'bb-1' }));
  const body = b64u(JSON.stringify({ iss: ISSUER, aud: AUDIENCE, iat: now, nbf: now - 60, exp: now + 4 * 3600, ...claims }));
  return `${head}.${body}.${b64u(sign('RSA-SHA256', Buffer.from(`${head}.${body}`), privateKey))}`;
}

/** The pipeline's client-credentials token, as the bank's provider would issue it. */
const token = jwt({ sub: 'pipeline', azp: 'pipeline', roles: ['TransactHub.Pipeline'] });

/** What the runtime's API host needs: the bank's issuer, audience and keys, and the bank's role map. */
export function runtimeApiEnv(): Record<string, string> {
  return {
    // The one bank the runtime serves (R-09-76), and where the pipeline may still change which release runs (R-09-78).
    TH_BANK: 'example-bank',
    Releases__MachineActivationEnvironments: 'test,dev,kit,uat,prod',
    Auth__Oidc__Issuer: ISSUER,
    Auth__Oidc__Audience: AUDIENCE,
    Auth__Oidc__SigningKeys: JSON.stringify({ keys: [{ ...publicKey.export({ format: 'jwk' }), kid: 'bb-1', use: 'sig', alg: 'RS256' }] }),
    Auth__RoleMap: JSON.stringify({ kind: 'runtime-role-map', grants: [{ claim: 'roles', value: 'TransactHub.Pipeline', roles: ['release-importer', 'release-activator', 'release-reader', 'values-reader', 'payment-intake'], tenants: ['*'] }] }),
    // The runs' branch checkouts are temporary folders: the only folders POST /import-branch may read (R-09-58).
    Releases__ImportRoots: os.tmpdir(),
  };
}

/** What a deploy needs so its workers may read the runtime's values: the token, by reference. */
export function runtimeClientEnv(): Record<string, string> {
  return { [TOKEN_VAR]: token, TH_RUNTIME_TOKEN: `env:${TOKEN_VAR}` };
}

/** The pipeline's headers on a call to the runtime. */
export function runtimeHeaders(extra: Record<string, string> = {}): Record<string, string> {
  return { authorization: `Bearer ${token}`, ...extra };
}
