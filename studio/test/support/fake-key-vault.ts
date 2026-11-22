import { randomUUID } from 'node:crypto';
import { createHttpHeaders, type HttpClient, type PipelineRequest, type PipelineResponse } from '@azure/core-rest-pipeline';

// A small fake of Azure Key Vault and of the Microsoft identity platform's token endpoint, in the process: the
// transport (an @azure/core-rest-pipeline HttpClient) that the official SDK, @azure/keyvault-secrets with
// @azure/identity, is given in place of the network. It answers the REST protocol the SDK speaks: a first request with no
// token gets 401 and the Bearer challenge (authorization, resource); the client-credentials grant at
// <authority>/<tenant>/oauth2/v2.0/token gives a token for the right client secret only; GET
// /secrets/{name}[/{version}]?api-version= answers the secret bundle, 404 SecretNotFound or 403 Forbidden. There is no
// official Key Vault emulator. Every value here is made by the test that uses it.

export const AUTHORITY = 'https://login.fake-authority.test';

export interface FakeKeyVault {
  httpClient: HttpClient;
  /** Sets a secret; returns its new version (32 hex digits). */
  set(vault: string, name: string, value: string): string;
  /** A token the fake honours, as a managed identity would get one. */
  issue(granted?: boolean): string;
  /** Every request the SDK sent, by method and URL (no bodies). */
  requests: string[];
  /** The client secrets the token endpoint was sent. */
  clientSecretsSeen: string[];
  /** Unknown requests, for a failing test to show. */
  unknown: string[];
}

export function fakeKeyVault(opts: { tenantId: string; clientId?: string; clientSecret?: string }): FakeKeyVault {
  const secrets = new Map<string, { version: string; value: string }[]>();
  const tokens = new Map<string, boolean>();
  const requests: string[] = [];
  const clientSecretsSeen: string[] = [];
  const unknown: string[] = [];

  const respond = (request: PipelineRequest, status: number, body: unknown, headers: Record<string, string> = {}): PipelineResponse => ({
    request,
    status,
    headers: createHttpHeaders({ 'content-type': 'application/json; charset=utf-8', ...headers }),
    bodyAsText: body === undefined ? '' : JSON.stringify(body),
  });
  const challenge = { 'www-authenticate': `Bearer authorization="${AUTHORITY}/${opts.tenantId}", resource="https://vault.azure.net"` };
  const issue = (granted = true) => {
    const t = `synthetic-token-${randomUUID()}`;
    tokens.set(t, granted);
    return t;
  };

  const sendRequest = async (request: PipelineRequest): Promise<PipelineResponse> => {
    const url = new URL(request.url);
    requests.push(`${request.method} ${url.origin}${url.pathname}`);
    const tenantBase = `/${opts.tenantId}`;
    // MSAL asks the public cloud's instance discovery about an authority it does not know; the fake answers for it.
    if (url.origin === 'https://login.microsoftonline.com' && url.pathname === '/common/discovery/instance') {
      const host = new URL(AUTHORITY).host;
      return respond(request, 200, { tenant_discovery_endpoint: `${AUTHORITY}${tenantBase}/v2.0/.well-known/openid-configuration`, 'api-version': '1.1', metadata: [{ preferred_network: host, preferred_cache: host, aliases: [host] }] });
    }
    if (url.origin === AUTHORITY) {
      if (url.pathname === `${tenantBase}/v2.0/.well-known/openid-configuration`)
        return respond(request, 200, {
          issuer: `${AUTHORITY}${tenantBase}/v2.0`,
          token_endpoint: `${AUTHORITY}${tenantBase}/oauth2/v2.0/token`,
          authorization_endpoint: `${AUTHORITY}${tenantBase}/oauth2/v2.0/authorize`,
          end_session_endpoint: `${AUTHORITY}${tenantBase}/oauth2/v2.0/logout`,
          jwks_uri: `${AUTHORITY}${tenantBase}/discovery/v2.0/keys`,
        });
      if (url.pathname === `${tenantBase}/oauth2/v2.0/token` && request.method === 'POST') {
        const form = new URLSearchParams(typeof request.body === 'string' ? request.body : '');
        clientSecretsSeen.push(form.get('client_secret') ?? '');
        if (form.get('grant_type') !== 'client_credentials' || form.get('client_id') !== opts.clientId || form.get('client_secret') !== opts.clientSecret)
          return respond(request, 401, { error: 'invalid_client', error_description: 'AADSTS7000215: Invalid client secret provided.' });
        return respond(request, 200, { token_type: 'Bearer', expires_in: 3599, ext_expires_in: 3599, access_token: issue() });
      }
    }
    const m = /^https:\/\/([a-z0-9-]+)\.vault\.azure\.net$/.exec(url.origin);
    const p = /^\/secrets\/([A-Za-z0-9-]+)\/?([0-9a-f]{32})?\/?$/.exec(url.pathname);
    if (m && p && request.method === 'GET' && url.searchParams.has('api-version')) {
      const auth = request.headers.get('authorization') ?? '';
      const token = auth.startsWith('Bearer ') ? auth.slice(7) : '';
      if (!tokens.has(token)) return respond(request, 401, { error: { code: 'Unauthorized', message: 'AKV10000: Request is missing a Bearer or PoP token.' } }, challenge);
      if (!tokens.get(token)) return respond(request, 403, { error: { code: 'Forbidden', message: 'The user, group or application does not have secrets get permission.' } });
      const versions = secrets.get(`${m[1]}/${p[1]}`) ?? [];
      const s = p[2] ? versions.find((v) => v.version === p[2]) : versions[versions.length - 1];
      if (!s) return respond(request, 404, { error: { code: 'SecretNotFound', message: `A secret with (name/id) ${p[1]} was not found in this key vault.` } });
      return respond(request, 200, { value: s.value, id: `${url.origin}/secrets/${p[1]}/${s.version}`, attributes: { enabled: true, created: 1, updated: 1, recoveryLevel: 'Recoverable+Purgeable' }, tags: {} });
    }
    unknown.push(`${request.method} ${request.url}`);
    return respond(request, 400, { error: { code: 'BadRequest', message: 'The fake does not answer this request.' } });
  };

  return {
    httpClient: { sendRequest },
    set(vault, name, value) {
      const version = randomUUID().replace(/-/g, '');
      const key = `${vault}/${name}`;
      secrets.set(key, [...(secrets.get(key) ?? []), { version, value }]);
      return version;
    },
    issue,
    requests,
    clientSecretsSeen,
    unknown,
  };
}
