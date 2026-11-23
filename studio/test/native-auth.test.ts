import { execFileSync, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as http from 'node:http';
import * as https from 'node:https';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { forgetNativeToken, nativeAuthHeaders, nativeFetch } from '../src/interp/native-auth.js';

// How a worker authenticates to the native host (R-08-21, Issue I-121): its own client-credentials token, fetched from the
// bank's identity provider and fetched again before it expires; and, as an option, mutual TLS with a client certificate by
// reference. The CA, the host's certificate and the worker's are made here, at test time, and removed after.

describe('the worker\'s client credentials for the native host', () => {
  let idp: http.Server;
  let url = '';
  let issued = 0;
  const forms: URLSearchParams[] = [];
  let life = 300;
  beforeAll(async () => {
    idp = http.createServer(async (req, res) => {
      let raw = '';
      for await (const c of req) raw += c;
      const f = new URLSearchParams(raw);
      forms.push(f);
      if (f.get('client_secret') !== 'synthetic-client-secret') {
        res.writeHead(401, { 'content-type': 'application/json' }).end(JSON.stringify({ error: 'invalid_client' }));
        return;
      }
      issued++;
      res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({ access_token: `synthetic-access-${issued}`, token_type: 'Bearer', expires_in: life }));
    });
    await new Promise<void>((r) => idp.listen(0, '127.0.0.1', () => r()));
    url = `http://127.0.0.1:${(idp.address() as any).port}/token`;
  });
  afterAll(() => idp.close());

  const env = () => ({ TH_NATIVE_TOKEN_URL: url, TH_NATIVE_CLIENT_ID: 'worker-example-bank', TH_NATIVE_CLIENT_SECRET: 'env:TH_TEST_NATIVE_SECRET', TH_NATIVE_SCOPE: 'transacthub-native/.default' });

  it('gets its own token by client credentials, keeps it while it is fresh, and gets a new one before it expires', async () => {
    process.env.TH_TEST_NATIVE_SECRET = 'synthetic-client-secret';
    try {
      forgetNativeToken();
      expect(await nativeAuthHeaders(env())).toEqual({ authorization: 'Bearer synthetic-access-1' });
      expect(await nativeAuthHeaders(env())).toEqual({ authorization: 'Bearer synthetic-access-1' });
      expect(forms[0].get('grant_type')).toBe('client_credentials');
      expect(forms[0].get('client_id')).toBe('worker-example-bank');
      expect(forms[0].get('scope')).toBe('transacthub-native/.default');
      // A token with a short life is fetched again before it ends.
      life = 10;
      forgetNativeToken();
      expect(await nativeAuthHeaders(env())).toEqual({ authorization: 'Bearer synthetic-access-2' });
      expect(await nativeAuthHeaders(env())).toEqual({ authorization: 'Bearer synthetic-access-2' });
      await new Promise((r) => setTimeout(r, 1100));
      expect(await nativeAuthHeaders(env())).toEqual({ authorization: 'Bearer synthetic-access-3' });
    } finally {
      delete process.env.TH_TEST_NATIVE_SECRET;
      life = 300;
    }
  });

  it('a refused secret is an error, and the secret is never sent anywhere but the token endpoint', async () => {
    process.env.TH_TEST_NATIVE_SECRET = 'wrong-synthetic-secret';
    try {
      forgetNativeToken();
      await expect(nativeAuthHeaders(env())).rejects.toThrow(/refused the worker's client credentials \(401\)/);
    } finally {
      delete process.env.TH_TEST_NATIVE_SECRET;
    }
  });
});

const hasOpenssl = spawnSync('openssl', ['version']).status === 0;

describe.runIf(hasOpenssl)('mutual TLS to the native host (R-08-21)', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'th-mtls-'));
  afterAll(() => fs.rmSync(dir, { recursive: true, force: true }));
  const ssl = (...a: string[]) => execFileSync('openssl', a, { cwd: dir, stdio: 'pipe' });
  let server: https.Server;
  let url = '';
  const seen: (string | undefined)[] = [];
  const file = (n: string) => `file:${path.join(dir, n)}`;

  beforeAll(async () => {
    // A dev CA, made now: the host's certificate for 127.0.0.1 and the worker's client certificate, both signed by it, and a
    // stranger's certificate from another CA.
    for (const ca of ['ca', 'other-ca']) ssl('req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-nodes', '-keyout', `${ca}.key`, '-out', `${ca}.pem`, '-days', '1', '-subj', `/CN=TransactHub test ${ca}`);
    fs.writeFileSync(path.join(dir, 'server.ext'), 'subjectAltName=IP:127.0.0.1,DNS:localhost\nextendedKeyUsage=serverAuth\n');
    fs.writeFileSync(path.join(dir, 'client.ext'), 'extendedKeyUsage=clientAuth\n');
    for (const [name, ca, ext] of [['server', 'ca', 'server.ext'], ['worker', 'ca', 'client.ext'], ['stranger', 'other-ca', 'client.ext']]) {
      ssl('req', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-nodes', '-keyout', `${name}.key`, '-out', `${name}.csr`, '-subj', `/CN=${name}-example-bank`);
      ssl('x509', '-req', '-in', `${name}.csr`, '-CA', `${ca}.pem`, '-CAkey', `${ca}.key`, '-CAcreateserial', '-out', `${name}.pem`, '-days', '1', '-extfile', ext);
    }
    server = https.createServer({ key: fs.readFileSync(path.join(dir, 'server.key')), cert: fs.readFileSync(path.join(dir, 'server.pem')), ca: fs.readFileSync(path.join(dir, 'ca.pem')), requestCert: true, rejectUnauthorized: true }, (req, res) => {
      seen.push((req.socket as any).getPeerCertificate?.()?.subject?.CN);
      res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({ kind: 'result', outcome: 'mapped', writes: {} }));
    });
    await new Promise<void>((r) => server.listen(0, '127.0.0.1', () => r()));
    url = `https://127.0.0.1:${(server.address() as any).port}/native`;
  });
  afterAll(() => server?.close());

  const call = (env: Record<string, string>) => nativeFetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' }, env).then((r) => r.json());

  it('presents the worker\'s certificate, by reference, and checks the host\'s against the configured CA', async () => {
    const r = await call({ TH_NATIVE_CLIENT_CERT: file('worker.pem'), TH_NATIVE_CLIENT_KEY: file('worker.key'), TH_NATIVE_CA: file('ca.pem'), TH_SECRET_FILE_ROOTS: dir });
    expect(r).toMatchObject({ kind: 'result', outcome: 'mapped' });
    expect(seen).toEqual(['worker-example-bank']);
  });

  it('is refused without a certificate of the host\'s CA, and refuses a host its CA did not sign', async () => {
    await expect(call({ TH_NATIVE_CLIENT_CERT: file('stranger.pem'), TH_NATIVE_CLIENT_KEY: file('stranger.key'), TH_NATIVE_CA: file('ca.pem'), TH_SECRET_FILE_ROOTS: dir })).rejects.toThrow();
    await expect(call({ TH_NATIVE_CLIENT_CERT: file('worker.pem'), TH_NATIVE_CLIENT_KEY: file('worker.key'), TH_NATIVE_CA: file('other-ca.pem'), TH_SECRET_FILE_ROOTS: dir })).rejects.toThrow();
    expect(seen).toEqual(['worker-example-bank']);
  });
});
