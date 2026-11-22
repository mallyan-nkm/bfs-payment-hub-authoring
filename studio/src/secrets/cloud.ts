import { bootstrapSecret, SecretUnavailable, SecretValue, type SecretStores } from './secrets.js';

// The cloud secret stores (R-06-25), on their official SDKs, loaded only when a reference names them.
//   akv:<vault-name>/<secret-name>[#version]   Azure Key Vault, https://<vault-name>.<TH_AKV_DNS_SUFFIX, vault.azure.net>/
//   aws-sm:<region>/<secret-id>[#json-field]   AWS Secrets Manager: the secret string, or one field of its JSON object
//   aws-ssm:<region>/<parameter-name>          AWS Systems Manager Parameter Store: a SecureString, decrypted
// Azure signs in with the first of these that is configured, in DefaultAzureCredential's order: client credentials
// (AZURE_TENANT_ID, AZURE_CLIENT_ID and TH_AKV_CLIENT_SECRET, a reference, never the secret itself), workload identity
// (AZURE_FEDERATED_TOKEN_FILE), managed identity (AZURE_CLIENT_ID for a user-assigned one). AZURE_CLIENT_SECRET is
// refused: it would put the client secret itself in the environment. AWS signs in with its default credential chain
// (environment, web identity for IRSA, the container or instance role). No message carries a value or a store's text.

const REGION = '[a-z]{2}(?:-[a-z]+)+-\\d{1,2}';
const AKV = /^([A-Za-z][A-Za-z0-9-]{1,22}[A-Za-z0-9])\/([A-Za-z0-9-]{1,127})(?:#([0-9A-Fa-f]{32}))?$/;
const AWS_SM = new RegExp(`^(${REGION})/([A-Za-z0-9/_+=.@:!-]{1,2048}?)(?:#([^#]+))?$`);
const AWS_SSM = new RegExp(`^(${REGION})/(/?[A-Za-z0-9_.\\-/]{1,2048})$`);

/** A string field of a JSON object, or a number or boolean as its JSON text. */
function jsonField(ref: string, text: string, field: string): SecretValue {
  let doc: any;
  try {
    doc = JSON.parse(text);
  } catch {
    throw new SecretUnavailable(ref, `names field ${field}, but the secret is not a JSON object`);
  }
  const v = doc && typeof doc === 'object' && !Array.isArray(doc) ? doc[field] : undefined;
  if (typeof v === 'string' && v) return new SecretValue(v);
  if (typeof v === 'number' || typeof v === 'boolean') return new SecretValue(String(v));
  throw new SecretUnavailable(ref, `has no field ${field}`);
}

/** The errors @azure/identity throws when no credential could get a token. */
const AZURE_CREDENTIAL_ERRORS = new Set(['CredentialUnavailableError', 'AuthenticationError', 'AggregateAuthenticationError', 'AuthenticationRequiredError']);

async function azureCredential(stores: SecretStores, ref: string): Promise<any> {
  if (stores.deps.azure?.credential) return stores.deps.azure.credential;
  const env = stores.env;
  const id = await import('@azure/identity');
  const common: any = { authorityHost: env.AZURE_AUTHORITY_HOST || undefined, httpClient: stores.deps.azure?.httpClient };
  if (env.TH_AKV_CLIENT_SECRET) {
    if (!env.AZURE_TENANT_ID || !env.AZURE_CLIENT_ID) throw new SecretUnavailable(ref, 'could not be read: TH_AKV_CLIENT_SECRET is set, and AZURE_TENANT_ID and AZURE_CLIENT_ID must be too');
    let secret: SecretValue;
    try {
      secret = bootstrapSecret('TH_AKV_CLIENT_SECRET', env.TH_AKV_CLIENT_SECRET, env);
    } catch (e: any) {
      throw new SecretUnavailable(ref, `could not be read: the Azure client secret: ${e.message}`);
    }
    return new id.ClientSecretCredential(env.AZURE_TENANT_ID, env.AZURE_CLIENT_ID, secret.reveal(), common);
  }
  if (env.AZURE_CLIENT_SECRET) throw new SecretUnavailable(ref, 'could not be read: AZURE_CLIENT_SECRET holds a client secret itself, which is not read: set TH_AKV_CLIENT_SECRET to a reference to it (env: or file:)');
  if (env.AZURE_FEDERATED_TOKEN_FILE) return new id.WorkloadIdentityCredential({ ...common, tenantId: env.AZURE_TENANT_ID, clientId: env.AZURE_CLIENT_ID, tokenFilePath: env.AZURE_FEDERATED_TOKEN_FILE });
  return new id.ManagedIdentityCredential({ ...common, clientId: env.AZURE_CLIENT_ID || undefined });
}

/** Which Azure credential this environment signs in with, by name: for the installation's checks and the tests. */
export async function azureCredentialKind(stores: SecretStores): Promise<string> {
  return (await azureCredential(stores, 'akv:(credential)')).constructor.name;
}

export async function fromAzureKeyVault(stores: SecretStores, ref: string, locator: string): Promise<SecretValue> {
  const m = AKV.exec(locator);
  if (!m) throw new SecretUnavailable(ref, 'is not akv:<vault-name>/<secret-name>[#version] (a vault name of 3 to 24 letters, digits and hyphens; a version of 32 hex digits)');
  const [, vault, name, version] = m;
  const suffix = stores.env.TH_AKV_DNS_SUFFIX || 'vault.azure.net';
  const url = `https://${vault.toLowerCase()}.${suffix}/`;
  let credential: any;
  try {
    credential = await stores.client('akv-credential', () => azureCredential(stores, ref));
  } catch (e) {
    stores.forget('akv-credential');
    throw e;
  }
  const { SecretClient } = await import('@azure/keyvault-secrets');
  const client = stores.client(`akv:${url}`, () => new SecretClient(url, credential, { httpClient: stores.deps.azure?.httpClient as any, retryOptions: { maxRetries: 2 } }));
  let value: string | undefined;
  try {
    value = (await client.getSecret(name, version ? { version } : {})).value;
  } catch (e: any) {
    if (e instanceof SecretUnavailable) throw e;
    if (e?.name === 'RestError' && typeof e.statusCode === 'number') {
      if (e.statusCode === 404) throw new SecretUnavailable(ref, `does not exist: Key Vault answered 404${e.code ? ` ${e.code}` : ''}`);
      if (e.statusCode === 401 || e.statusCode === 403) throw new SecretUnavailable(ref, `could not be read: Key Vault refused access (${e.statusCode}${e.code ? ` ${e.code}` : ''})`);
      throw new SecretUnavailable(ref, `could not be read: Key Vault answered ${e.statusCode}`);
    }
    if (AZURE_CREDENTIAL_ERRORS.has(e?.name))
      throw new SecretUnavailable(ref, 'could not be read: no Azure credential could get a token (client credentials, workload identity or managed identity)');
    throw new SecretUnavailable(ref, 'could not be read: Key Vault did not answer');
  }
  if (!value) throw new SecretUnavailable(ref, 'is empty');
  return new SecretValue(value);
}

function awsReason(service: string, e: any): string {
  const status = e?.$metadata?.httpStatusCode;
  if (e?.name === 'CredentialsProviderError' || /credential/i.test(e?.name ?? '')) return 'could not be read: no AWS credentials were found (the default chain: environment, web identity, the container or instance role)';
  if (typeof status === 'number') return `could not be read: ${service} answered ${status}${e?.name ? ` ${e.name}` : ''}`;
  return `could not be read: ${service} did not answer`;
}

const awsClientConfig = (stores: SecretStores, region: string) => ({
  region,
  endpoint: stores.deps.aws?.endpoint,
  credentials: stores.deps.aws?.credentials,
  maxAttempts: 3,
});

export async function fromAwsSecretsManager(stores: SecretStores, ref: string, locator: string): Promise<SecretValue> {
  const m = AWS_SM.exec(locator);
  if (!m) throw new SecretUnavailable(ref, 'is not aws-sm:<region>/<secret-id>[#json-field]');
  const [, region, secretId, field] = m;
  const sm = await import('@aws-sdk/client-secrets-manager');
  const client = stores.client(`aws-sm:${region}`, () => new sm.SecretsManagerClient(awsClientConfig(stores, region)));
  let out: { SecretString?: string; SecretBinary?: Uint8Array };
  try {
    out = await client.send(new sm.GetSecretValueCommand({ SecretId: secretId }));
  } catch (e: any) {
    if (e?.name === 'ResourceNotFoundException') throw new SecretUnavailable(ref, 'does not exist: Secrets Manager answered ResourceNotFoundException');
    throw new SecretUnavailable(ref, awsReason('Secrets Manager', e));
  }
  if (typeof out.SecretString !== 'string') throw new SecretUnavailable(ref, 'is a binary secret: keep a secret the runtime reads as a secret string');
  if (field) return jsonField(ref, out.SecretString, field);
  if (!out.SecretString) throw new SecretUnavailable(ref, 'is empty');
  return new SecretValue(out.SecretString);
}

/** A parameter's name: a name with a '/' in it is a path, and a path starts with '/'. */
export const ssmName = (name: string) => (name.includes('/') && !name.startsWith('/') ? '/' + name : name.replace(/^\/\/+/, '/'));

export async function fromAwsParameterStore(stores: SecretStores, ref: string, locator: string): Promise<SecretValue> {
  const m = AWS_SSM.exec(locator);
  if (!m) throw new SecretUnavailable(ref, 'is not aws-ssm:<region>/<parameter-name>');
  const [, region, raw] = m;
  const ssm = await import('@aws-sdk/client-ssm');
  const client = stores.client(`aws-ssm:${region}`, () => new ssm.SSMClient(awsClientConfig(stores, region)));
  let p: { Type?: string; Value?: string } | undefined;
  try {
    p = (await client.send(new ssm.GetParameterCommand({ Name: ssmName(raw), WithDecryption: true }))).Parameter;
  } catch (e: any) {
    if (e?.name === 'ParameterNotFound') throw new SecretUnavailable(ref, 'does not exist: Parameter Store answered ParameterNotFound');
    throw new SecretUnavailable(ref, awsReason('Parameter Store', e));
  }
  if (p?.Type !== 'SecureString') throw new SecretUnavailable(ref, `is a ${p?.Type ?? 'parameter of no type'}, not a SecureString: keep a secret in a SecureString`);
  if (!p.Value) throw new SecretUnavailable(ref, 'is empty');
  return new SecretValue(p.Value);
}
