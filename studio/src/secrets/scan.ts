import { parseYamlAll } from '../core/yaml.js';
import { isSecretRef, REF_FORMS } from './secrets.js';

// Check L37 (R-06-23): no binding, configuration or package carries a literal secret. A key that names a secret
// (password, passphrase, token, apiKey, privateKey, clientSecret, credential, and the like) holds a reference
// (`env:`, `file:`, `vault:`, `akv:`, `aws-sm:`, `aws-ssm:`) or nothing; `secretRef` holds a reference; and no text anywhere is a private key.
// The same rule is in spec/tools/validate.py.

/** A key that names a secret: the word itself, or a camel-case name that ends with it (`gatewayPassword`). */
export const SECRET_KEY = /(^|[a-z0-9_])(password|passwd|passphrase|secret|token|apikey|api_key|accesskey|access_key|privatekey|private_key|clientsecret|client_secret|credentials?)$/i;
export const PRIVATE_KEY_PEM = /-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----/;

export interface LiteralSecret {
  file: string;
  at: string;
  why: string;
}

/** The literal secrets in one document, by the path to each. */
export function literalSecretsIn(doc: unknown, file: string): LiteralSecret[] {
  const out: LiteralSecret[] = [];
  const visit = (v: unknown, at: string, key?: string) => {
    if (typeof v === 'string') {
      if (PRIVATE_KEY_PEM.test(v)) out.push({ file, at, why: 'is a private key' });
      else if (key && /secretref$/i.test(key) && !isSecretRef(v)) out.push({ file, at, why: `${key} must be a reference (${REF_FORMS}), not a value` });
      else if (key && SECRET_KEY.test(key) && v.length > 0 && !isSecretRef(v)) out.push({ file, at, why: `${key} holds a value: name the secret by reference instead (secretRef: ${REF_FORMS})` });
      return;
    }
    if (typeof v === 'number' && key && SECRET_KEY.test(key)) {
      out.push({ file, at, why: `${key} holds a value: name the secret by reference instead` });
      return;
    }
    if (Array.isArray(v)) v.forEach((x, i) => visit(x, `${at}[${i}]`, key));
    else if (v && typeof v === 'object') for (const [k, x] of Object.entries(v)) visit(x, at ? `${at}.${k}` : k, k);
  };
  visit(doc, '');
  return out;
}

/** The literal secrets in a file's text: every YAML document in it, or, for any other file, a private key in its text. */
export function literalSecretsInText(text: string, file: string): LiteralSecret[] {
  if (/\.ya?ml$/i.test(file)) {
    let docs: unknown[];
    try {
      docs = parseYamlAll(text);
    } catch {
      docs = [];
    }
    const found = docs.flatMap((d) => literalSecretsIn(d, file));
    return found.length || !PRIVATE_KEY_PEM.test(text) ? found : [{ file, at: '', why: 'holds a private key' }];
  }
  if (/\.json$/i.test(file)) {
    try {
      return literalSecretsIn(JSON.parse(text), file);
    } catch {
      // not JSON after all: the text rule below
    }
  }
  return PRIVATE_KEY_PEM.test(text) ? [{ file, at: '', why: 'holds a private key' }] : [];
}
