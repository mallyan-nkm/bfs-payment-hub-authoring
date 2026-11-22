import { spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { inspect } from 'node:util';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { resolveSecret, SecretStores, SecretUnavailable, type Env, type SecretDeps } from '../src/secrets/secrets.js';
import { azureCredentialKind, ssmName } from '../src/secrets/cloud.js';
import { literalSecretsIn } from '../src/secrets/scan.js';
import { AUTHORITY, fakeKeyVault } from './support/fake-key-vault.js';

// The cloud secret stores (R-06-25, R-06-26): Azure Key Vault against an in-process fake of its REST protocol (there is
// no official emulator), AWS Secrets Manager and Parameter Store against LocalStack in a container of this run's own.
// Every value, token and key here is made by the test.

const synthetic = () => `synthetic-${randomUUID()}`;
const reasonOf = async (p: Promise<unknown>) => {
  const e = await p.then(
    () => undefined,
    (x) => x,
  );
  expect(e).toBeInstanceOf(SecretUnavailable);
  return e as SecretUnavailable;
};

describe('Azure Key Vault (akv:), on the official SDK against a fake of its REST protocol', () => {
  const tenantId = randomUUID();
  const clientId = randomUUID();
  const clientSecret = synthetic();
  const fake = fakeKeyVault({ tenantId, clientId, clientSecret });
  const value = synthetic();
  const older = synthetic();
  const olderVersion = fake.set('th-test-vault', 'signing-key', older);
  fake.set('th-test-vault', 'signing-key', value);
  const managed: SecretDeps = { azure: { httpClient: fake.httpClient, credential: { getToken: async () => ({ token: fake.issue(), expiresOnTimestamp: Date.now() + 3600_000 }) } } };

  it('reads the latest version and a named one, through the Bearer challenge', async () => {
    expect((await resolveSecret('akv:th-test-vault/signing-key', {}, managed)).reveal()).toBe(value);
    expect((await resolveSecret(`akv:th-test-vault/signing-key#${olderVersion}`, {}, managed)).reveal()).toBe(older);
    // The SDK asked once without a token, was challenged, and asked again with one.
    expect(fake.requests.filter((r) => r.startsWith('GET https://th-test-vault.vault.azure.net/secrets/signing-key')).length).toBeGreaterThanOrEqual(3);
    expect(fake.unknown).toEqual([]);
  });

  it('signs in with client credentials whose secret is itself a reference, and says why without a value when it cannot', async () => {
    const env: Env = { AZURE_TENANT_ID: tenantId, AZURE_CLIENT_ID: clientId, AZURE_AUTHORITY_HOST: AUTHORITY, TH_AKV_CLIENT_SECRET: 'env:TH_TEST_AKV_CLIENT_SECRET', TH_TEST_AKV_CLIENT_SECRET: clientSecret };
    const deps: SecretDeps = { azure: { httpClient: fake.httpClient } };
    expect((await resolveSecret('akv:th-test-vault/signing-key', env, deps)).reveal()).toBe(value);
    expect(fake.clientSecretsSeen).toContain(clientSecret);
    expect(fake.unknown).toEqual([]);
    const wrong = synthetic();
    const e = await reasonOf(resolveSecret('akv:th-test-vault/signing-key', { ...env, TH_TEST_AKV_CLIENT_SECRET: wrong }, deps));
    expect(e.message).toBe('Secret akv:th-test-vault/signing-key could not be read: no Azure credential could get a token (client credentials, workload identity or managed identity)');
    for (const s of [wrong, clientSecret, value]) expect(String(e) + inspect(e)).not.toContain(s);
    // The client secret is a reference to env: or file:, never the secret itself, and never AZURE_CLIENT_SECRET.
    expect((await reasonOf(resolveSecret('akv:th-test-vault/signing-key', { ...env, TH_AKV_CLIENT_SECRET: wrong }, deps))).message).not.toContain(wrong);
    expect((await reasonOf(resolveSecret('akv:th-test-vault/signing-key', { AZURE_CLIENT_SECRET: wrong }, deps))).message).toContain('set TH_AKV_CLIENT_SECRET to a reference');
    expect((await reasonOf(resolveSecret('akv:th-test-vault/signing-key', { TH_AKV_CLIENT_SECRET: 'env:X' }, deps))).message).toContain('AZURE_TENANT_ID and AZURE_CLIENT_ID');
  });

  it('names the reference only when the secret is missing, access is refused or the reference is malformed', async () => {
    expect((await reasonOf(resolveSecret('akv:th-test-vault/no-such-secret', {}, managed))).message).toBe('Secret akv:th-test-vault/no-such-secret does not exist: Key Vault answered 404 SecretNotFound');
    const refused: SecretDeps = { azure: { httpClient: fake.httpClient, credential: { getToken: async () => ({ token: fake.issue(false), expiresOnTimestamp: Date.now() + 3600_000 }) } } };
    const e = await reasonOf(resolveSecret('akv:th-test-vault/signing-key', {}, refused));
    expect(e.message).toBe('Secret akv:th-test-vault/signing-key could not be read: Key Vault refused access (403 Forbidden)');
    expect(String(e)).not.toContain(value);
    for (const bad of ['akv:v/secret', 'akv:th-test-vault', 'akv:th-test-vault/a_b', 'akv:th-test-vault/x#not-a-version'])
      expect((await reasonOf(resolveSecret(bad, {}, managed))).message).toContain('is not akv:<vault-name>/<secret-name>[#version]');
  });

  it('chooses its credential in DefaultAzureCredential order, client secrets by reference only', async () => {
    const kind = (env: Env) => azureCredentialKind(new SecretStores(env, { azure: { httpClient: fake.httpClient } }));
    expect(await kind({})).toBe('ManagedIdentityCredential');
    expect(await kind({ AZURE_CLIENT_ID: clientId })).toBe('ManagedIdentityCredential');
    expect(await kind({ AZURE_TENANT_ID: tenantId, AZURE_CLIENT_ID: clientId, AZURE_FEDERATED_TOKEN_FILE: '/var/run/secrets/azure/tokens/azure-identity-token' })).toBe('WorkloadIdentityCredential');
    expect(await kind({ AZURE_TENANT_ID: tenantId, AZURE_CLIENT_ID: clientId, TH_AKV_CLIENT_SECRET: 'env:S', S: synthetic() })).toBe('ClientSecretCredential');
  });
});

describe('a value read over the network is cached for a short time and read again when refused', () => {
  const fake = fakeKeyVault({ tenantId: randomUUID() });
  const first = synthetic();
  fake.set('th-cache-vault', 'token', first);
  let now = 1_000_000;
  const deps: SecretDeps = { now: () => now, azure: { httpClient: fake.httpClient, credential: { getToken: async () => ({ token: fake.issue(), expiresOnTimestamp: Date.now() + 3600_000 }) } } };
  const reads = () => fake.requests.filter((r) => r.includes('/secrets/token')).length;

  it('serves the cached value within TH_SECRET_CACHE_SECONDS and never after it', async () => {
    const stores = new SecretStores({ TH_SECRET_CACHE_SECONDS: '60' }, deps);
    expect((await stores.resolve('akv:th-cache-vault/token')).reveal()).toBe(first);
    const n = reads();
    const second = synthetic();
    fake.set('th-cache-vault', 'token', second);
    now += 59_000;
    expect((await stores.resolve('akv:th-cache-vault/token')).reveal()).toBe(first);
    expect(reads()).toBe(n);
    now += 2_000;
    expect((await stores.resolve('akv:th-cache-vault/token')).reveal()).toBe(second);
    // 0 caches nothing.
    const none = new SecretStores({ TH_SECRET_CACHE_SECONDS: '0' }, deps);
    await none.resolve('akv:th-cache-vault/token');
    const m = reads();
    await none.resolve('akv:th-cache-vault/token');
    expect(reads()).toBeGreaterThan(m);
    stores.close();
    none.close();
  });

  it('reads a refused credential again from its store, once, and uses the rotated value', async () => {
    const stores = new SecretStores({ TH_SECRET_CACHE_SECONDS: '300' }, deps);
    const old = (await stores.resolve('akv:th-cache-vault/token')).reveal();
    const rotated = synthetic();
    fake.set('th-cache-vault', 'token', rotated);
    const used: string[] = [];
    const refusedError = new Error('401 from the system the token opens');
    const result = await stores.withSecret(
      'akv:th-cache-vault/token',
      async (v) => {
        used.push(v.reveal());
        if (v.reveal() !== rotated) throw refusedError;
        return 'posted';
      },
      (e) => e === refusedError,
    );
    expect(result).toBe('posted');
    expect(used).toEqual([old, rotated]);
    // A failure that is not a refusal is not retried.
    await expect(stores.withSecret('akv:th-cache-vault/token', async () => Promise.reject(new Error('other')), () => false)).rejects.toThrow('other');
    stores.close();
  });

  it('every reference form is a reference to check L37, and nothing else is', () => {
    const ok = ['akv:v1/s', 'aws-sm:eu-west-1/th/db#password', 'aws-ssm:eu-west-1/th/token'];
    expect(literalSecretsIn({ bindings: ok.map((secretRef) => ({ settings: { secretRef } })) }, 'x.yaml')).toEqual([]);
    expect(literalSecretsIn({ settings: { secretRef: 'gcp:projects/x' } }, 'x.yaml').map((f) => f.at)).toEqual(['settings.secretRef']);
    expect(ssmName('th/token')).toBe('/th/token');
    expect(ssmName('/th/token')).toBe('/th/token');
    expect(ssmName('token')).toBe('token');
  });
});

const hasDocker = spawnSync('docker', ['version', '--format', '{{.Server.Version}}'], { encoding: 'utf8' }).status === 0;
export const LOCALSTACK_IMAGE = 'localstack/localstack:4.14.0';

describe.skipIf(!hasDocker)('AWS Secrets Manager (aws-sm:) and Parameter Store (aws-ssm:), on the official SDK against LocalStack', () => {
  const name = `th-secrets-localstack-${process.pid}-${randomUUID().slice(0, 8)}`;
  // LocalStack takes any credentials; these are made for this run.
  const credentials = { accessKeyId: `AKIA${randomUUID().replace(/-/g, '').slice(0, 16).toUpperCase()}`, secretAccessKey: synthetic() };
  let deps: SecretDeps;
  const region = 'eu-west-1';
  const value = synthetic();
  const fieldValue = synthetic();
  const paramValue = synthetic();
  const id = `th/test/${randomUUID().slice(0, 8)}`;
  let arn = '';

  beforeAll(async () => {
    const run = spawnSync('docker', ['run', '-d', '--rm', '--name', name, '-e', 'SERVICES=secretsmanager,ssm,kms', '-p', '127.0.0.1::4566', LOCALSTACK_IMAGE], { encoding: 'utf8' });
    if (run.status !== 0) throw new Error(`LocalStack did not start: ${run.stderr}`);
    const port = spawnSync('docker', ['port', name, '4566'], { encoding: 'utf8' }).stdout.trim().split('\n')[0].split(':').pop();
    const endpoint = `http://127.0.0.1:${port}`;
    for (let i = 0; i < 120; i++) {
      const ok = await fetch(`${endpoint}/_localstack/health`).then(
        async (r) => (r.ok ? ((await r.json()) as any).services?.secretsmanager === 'available' : false),
        () => false,
      );
      if (ok) break;
      await new Promise((r) => setTimeout(r, 500));
    }
    deps = { aws: { endpoint, credentials } };
    const sm = await import('@aws-sdk/client-secrets-manager');
    const ssm = await import('@aws-sdk/client-ssm');
    const smc = new sm.SecretsManagerClient({ region, endpoint, credentials });
    const ssmc = new ssm.SSMClient({ region, endpoint, credentials });
    arn = (await smc.send(new sm.CreateSecretCommand({ Name: id, SecretString: value }))).ARN!;
    await smc.send(new sm.CreateSecretCommand({ Name: `${id}-json`, SecretString: JSON.stringify({ password: fieldValue, port: 5432 }) }));
    await smc.send(new sm.CreateSecretCommand({ Name: `${id}-binary`, SecretBinary: Buffer.from(synthetic()) }));
    await ssmc.send(new ssm.PutParameterCommand({ Name: `/${id}/token`, Value: paramValue, Type: 'SecureString' }));
    await ssmc.send(new ssm.PutParameterCommand({ Name: `/${id}/plain`, Value: synthetic(), Type: 'String' }));
    smc.destroy();
    ssmc.destroy();
  });
  afterAll(() => {
    spawnSync('docker', ['rm', '-f', name]);
  });

  it('reads a secret string by name and by ARN, and one field of a JSON secret', async () => {
    expect((await resolveSecret(`aws-sm:${region}/${id}`, {}, deps)).reveal()).toBe(value);
    expect((await resolveSecret(`aws-sm:${region}/${arn}`, {}, deps)).reveal()).toBe(value);
    expect((await resolveSecret(`aws-sm:${region}/${id}-json#password`, {}, deps)).reveal()).toBe(fieldValue);
    expect((await resolveSecret(`aws-sm:${region}/${id}-json#port`, {}, deps)).reveal()).toBe('5432');
  });

  it('names the reference only when a secret is missing, in another region, binary, or has no such field', async () => {
    expect((await reasonOf(resolveSecret(`aws-sm:${region}/${id}-none`, {}, deps))).message).toBe(`Secret aws-sm:${region}/${id}-none does not exist: Secrets Manager answered ResourceNotFoundException`);
    expect((await reasonOf(resolveSecret(`aws-sm:us-east-1/${id}`, {}, deps))).message).toContain('does not exist');
    expect((await reasonOf(resolveSecret(`aws-sm:${region}/${id}-binary`, {}, deps))).message).toContain('is a binary secret');
    const e = await reasonOf(resolveSecret(`aws-sm:${region}/${id}-json#user`, {}, deps));
    expect(e.message).toBe(`Secret aws-sm:${region}/${id}-json#user has no field user`);
    expect(String(e)).not.toContain(fieldValue);
    expect((await reasonOf(resolveSecret(`aws-sm:${region}/${id}#password`, {}, deps))).message).toContain('is not a JSON object');
    expect((await reasonOf(resolveSecret(`aws-sm:nowhere/${id}`, {}, deps))).message).toContain('is not aws-sm:<region>/<secret-id>[#json-field]');
    const noCreds = await reasonOf(
      resolveSecret(`aws-sm:${region}/${id}`, {}, {
        aws: {
          endpoint: deps.aws!.endpoint,
          credentials: (async () => {
            throw Object.assign(new Error('no credentials in this test'), { name: 'CredentialsProviderError' });
          }) as any,
        },
      }),
    );
    expect(noCreds.message).toContain('no AWS credentials were found');
  });

  it('reads a SecureString parameter, decrypted, by its path, and refuses a plain String', async () => {
    expect((await resolveSecret(`aws-ssm:${region}/${id}/token`, {}, deps)).reveal()).toBe(paramValue);
    expect((await resolveSecret(`aws-ssm:${region}//${id}/token`, {}, deps)).reveal()).toBe(paramValue);
    expect((await reasonOf(resolveSecret(`aws-ssm:${region}/${id}/plain`, {}, deps))).message).toContain('is a String, not a SecureString');
    expect((await reasonOf(resolveSecret(`aws-ssm:${region}/${id}/none`, {}, deps))).message).toBe(`Secret aws-ssm:${region}/${id}/none does not exist: Parameter Store answered ParameterNotFound`);
  });

  it('caches what it read for the stores of one environment', async () => {
    const stores = new SecretStores({}, deps);
    const a = await stores.resolve(`aws-sm:${region}/${id}`);
    expect(await stores.resolve(`aws-sm:${region}/${id}`)).toBe(a);
    stores.invalidate(`aws-sm:${region}/${id}`);
    expect(await stores.resolve(`aws-sm:${region}/${id}`)).not.toBe(a);
    stores.close();
  });
});
