import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, sign, verify, type KeyObject } from 'node:crypto';
import { canonicalJson } from '../core/canonical.js';
import { isSecretRef, resolveSecret, resolveSecretSync, SecretUnavailable, type SecretValue } from '../secrets/secrets.js';

// The signature of a released package (R-09-23 to R-09-26). Beside the package's files the studio writes two more:
//   release.json  the statement: canonical JSON (RFC 8785) naming the release id, workflow, release, target, tenant,
//                 change set, signing key id, signing time and the SHA-256 of every other file of the package;
//   release.sig   an ECDSA P-256 signature with SHA-256 over the statement's bytes, DER encoded, in Base64.
// That is a detached blob signature, so a person checks one with standard tools:
//   openssl dgst -sha256 -verify signer.pem -signature <(base64 -d release.sig) release.json
//   cosign verify-blob --key signer.pem --signature release.sig release.json
// The signing key is read only by reference (TH_SIGNING_KEY = env:…, file:… or vault:…). The keys a deployment trusts
// are TH_TRUSTED_SIGNERS: the trusted-signers JSON itself, or a reference to it. The runtime verifies the same way
// (runtime/src/TransactHub.Security/Signing.cs).

export const STATEMENT_FILE = 'release.json';
export const SIGNATURE_FILE = 'release.sig';
export const FORMAT = 'th-release-statement/1';
/**
 * The kinds of signed package: a workflow package (built), and a bank's distribution and configuration release
 * (docs/runtime/PACKAGING-AND-DEPLOYMENT.md), which add their bill of materials and parameter contract as further
 * fields of the same statement (`extra`).
 */
export type PackageKind = 'workflow-package' | 'distribution' | 'configuration';
export const SIGNING_KEY_ENV = 'TH_SIGNING_KEY';
export const TRUSTED_SIGNERS_ENV = 'TH_TRUSTED_SIGNERS';

export const isSignatureFile = (p: string) => p === STATEMENT_FILE || p === SIGNATURE_FILE;

export class SignatureRejected extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'SignatureRejected';
  }
}

/** A package of a subflow or a segment alone: not a release the runtime takes, so it is not signed. */
export class NoRootWorkflow extends SignatureRejected {}

export interface ReleaseClaims {
  kind?: PackageKind;
  /** Fields a kind adds to the statement, signed with the rest (a distribution's billOfMaterials, parameterContract). */
  extra?: Record<string, unknown>;
  releaseId: string;
  workflow: string;
  release: string;
  target: string;
  tenant: string | null;
  changeSet: string | null;
}

export interface Verification {
  kind: PackageKind;
  keyId: string;
  statementSha256: string;
  changeSet: string | null;
  signedAt: string;
  files: number;
}

const sha256 = (b: Buffer | string) => createHash('sha256').update(b).digest('hex');
const bytes = (c: string | Buffer) => (typeof c === 'string' ? Buffer.from(c, 'utf8') : c);

/** A key's id: the first 32 hex digits of the SHA-256 of its public key (SubjectPublicKeyInfo, DER). */
export function keyIdOf(key: KeyObject): string {
  const pub = key.type === 'private' ? createPublicKey(key) : key;
  return sha256(pub.export({ type: 'spki', format: 'der' })).slice(0, 32);
}

function privateKey(pem: SecretValue): KeyObject {
  let k: KeyObject;
  try {
    k = createPrivateKey(pem.reveal());
  } catch {
    throw new SignatureRejected('The signing key is not a PEM private key');
  }
  if (k.asymmetricKeyType !== 'ec' || k.asymmetricKeyDetails?.namedCurve !== 'prime256v1') throw new SignatureRejected('The signing key is not an EC key on P-256');
  return k;
}

let primed: { ref: string; key: KeyObject } | undefined;

/**
 * Reads a signing key held in Vault ahead of time, so signing can stay synchronous. env: and file: keys are read at each
 * signing instead, so a rotated key file takes effect with no restart.
 */
export async function primeSigningKey(env = process.env): Promise<void> {
  const ref = env[SIGNING_KEY_ENV];
  if (ref && isSecretRef(ref) && ref.startsWith('vault:')) primed = { ref, key: privateKey(await resolveSecret(ref, env)) };
}

/** The studio's signing key, from TH_SIGNING_KEY. Without one, nothing is released (R-09-23). */
export function signingKey(env = process.env): KeyObject {
  const ref = env[SIGNING_KEY_ENV];
  if (!ref) throw new SignatureRejected(`Set ${SIGNING_KEY_ENV} to a reference to the studio's signing key (env:NAME, file:/path or vault:mount/path#field): every released package is signed (R-09-23)`);
  if (!isSecretRef(ref)) throw new SignatureRejected(`${SIGNING_KEY_ENV} must be a reference to the key (env:NAME, file:/path or vault:…), never the key itself`);
  if (primed?.ref === ref) return primed.key;
  try {
    return privateKey(resolveSecretSync(ref, env));
  } catch (e) {
    if (e instanceof SecretUnavailable) throw new SignatureRejected(`The signing key could not be read: ${e.message}`);
    throw e;
  }
}

/** Signs a package's files: returns them with the statement and its signature added. */
export function signFiles(files: Record<string, string | Buffer>, claims: ReleaseClaims, key: KeyObject = signingKey(), signedAt = new Date()): Record<string, string | Buffer> {
  const statement = {
    ...(claims.extra ?? {}),
    format: FORMAT,
    kind: claims.kind ?? 'workflow-package',
    releaseId: claims.releaseId,
    workflow: claims.workflow,
    release: claims.release,
    target: claims.target,
    tenant: claims.tenant ?? null,
    changeSet: claims.changeSet ?? null,
    keyId: keyIdOf(key),
    signedAt: signedAt.toISOString(),
    files: Object.keys(files).filter((p) => !isSignatureFile(p)).sort().map((p) => ({ path: p, sha256: sha256(bytes(files[p])) })),
  };
  const text = canonicalJson(statement);
  const sig = sign('sha256', Buffer.from(text, 'utf8'), { key, dsaEncoding: 'der' });
  return { ...files, [STATEMENT_FILE]: text, [SIGNATURE_FILE]: sig.toString('base64') + '\n' };
}

/** The claims of an export package's signature: the runtime stores it as `<root workflow>/<target>@<release>`. */
export function claimsOf(manifest: any, changeSet?: string): ReleaseClaims {
  const flows = (manifest.flows ?? []).filter((f: any) => f.kind === 'workflow');
  const parts = ['leg', 'segment', 'subflow', 'handler'];
  const root = [flows.filter((f: any) => !parts.includes(f.wfKind)), flows.filter((f: any) => f.wfKind === 'leg')].find((t) => t.length === 1)?.[0];
  if (!root) throw new NoRootWorkflow('The package has no one root workflow to sign it as');
  // The change set the root workflow's release was published in, when the caller does not name the one publishing it.
  const cs = changeSet ?? (manifest.changeSets ?? []).find((c: any) => (c.members ?? []).includes(`${root.name}@${root.release}`))?.id ?? null;
  return { releaseId: `${root.name}/${manifest.target}@${root.release}`, workflow: root.name, release: root.release, target: manifest.target, tenant: manifest.tenant ?? null, changeSet: cs };
}

export interface TrustedSigners {
  keys: Map<string, { publicKey: KeyObject; retiredAt?: string }>;
  revoked: Map<string, { revokedAt?: string; reason?: string }>;
}

/** The trusted-signers document: { kind: trusted-signers, keys: [{ keyId, publicKey, retiredAt? }], revoked: [{ keyId, revokedAt, reason? }] }. */
export function parseTrustedSigners(json: string): TrustedSigners {
  let d: any;
  try {
    d = JSON.parse(json);
  } catch {
    throw new SignatureRejected('The trusted signers are not JSON');
  }
  if (d?.kind !== 'trusted-signers') throw new SignatureRejected('The trusted signers document is not of kind trusted-signers');
  const keys = new Map<string, { publicKey: KeyObject; retiredAt?: string }>();
  for (const k of d.keys ?? []) {
    let pub: KeyObject;
    try {
      pub = createPublicKey(k.publicKey);
    } catch {
      throw new SignatureRejected(`Trusted key ${k.keyId} is not a PEM public key`);
    }
    if (pub.asymmetricKeyDetails?.namedCurve !== 'prime256v1') throw new SignatureRejected(`Trusted key ${k.keyId} is not on P-256`);
    if (keyIdOf(pub) !== k.keyId) throw new SignatureRejected(`Trusted key ${k.keyId} is not the id of its public key (${keyIdOf(pub)})`);
    keys.set(k.keyId, { publicKey: pub, retiredAt: k.retiredAt });
  }
  const revoked = new Map<string, { revokedAt?: string; reason?: string }>((d.revoked ?? []).map((r: any) => [r.keyId, { revokedAt: r.revokedAt, reason: r.reason }]));
  return { keys, revoked };
}

/** The signers this deployment trusts, from TH_TRUSTED_SIGNERS: the JSON, or a reference to it. Read at each use. */
export async function trustedSigners(env = process.env): Promise<TrustedSigners> {
  const v = env[TRUSTED_SIGNERS_ENV];
  if (!v) throw new SignatureRejected(`Set ${TRUSTED_SIGNERS_ENV}: the signers whose packages may be deployed (R-09-26)`);
  return parseTrustedSigners(v.trimStart().startsWith('{') ? v : (await resolveSecret(v, env)).reveal());
}

/**
 * Verifies a package's files against the trusted signers. It refuses a package with no signature, a key not trusted or
 * revoked, a signature that does not verify, a key used after it retired, a statement for another release or tenant, and
 * any file missing, extra or changed.
 */
export function verifyFiles(files: Record<string, string | Buffer>, expected: { releaseId: string; tenant: string | null; kind?: PackageKind }, trust: TrustedSigners): Verification {
  const st = files[STATEMENT_FILE];
  const sigText = files[SIGNATURE_FILE];
  if (st === undefined || sigText === undefined) throw new SignatureRejected(`The package is not signed: it has no ${STATEMENT_FILE} and ${SIGNATURE_FILE}`);
  const stBytes = bytes(st);
  let s: any;
  try {
    s = JSON.parse(stBytes.toString('utf8'));
  } catch {
    throw new SignatureRejected(`${STATEMENT_FILE} is not JSON`);
  }
  if (s?.format !== FORMAT) throw new SignatureRejected(`${STATEMENT_FILE} is not a ${FORMAT} statement`);
  const kind = expected.kind ?? 'workflow-package';
  if (s.kind !== kind) throw new SignatureRejected(`The signature is for a ${s.kind ?? 'package of no kind'}, not a ${kind}`);
  const revoked = trust.revoked.get(s.keyId);
  if (revoked) throw new SignatureRejected(`The package is signed by key ${s.keyId}, which was revoked on ${revoked.revokedAt ?? 'an unknown date'}`);
  const signer = trust.keys.get(s.keyId);
  if (!signer) throw new SignatureRejected(`The package is signed by key ${s.keyId}, which this deployment does not trust`);
  const sig = Buffer.from(bytes(sigText).toString('ascii').trim(), 'base64');
  if (!verify('sha256', stBytes, { key: signer.publicKey, dsaEncoding: 'der' }, sig)) throw new SignatureRejected(`The signature does not verify with key ${s.keyId}: ${STATEMENT_FILE} or ${SIGNATURE_FILE} was changed`);
  if (signer.retiredAt && Date.parse(s.signedAt) > Date.parse(signer.retiredAt)) throw new SignatureRejected(`The package was signed on ${s.signedAt} by key ${s.keyId}, which was retired on ${signer.retiredAt}`);
  if (s.releaseId !== expected.releaseId) throw new SignatureRejected(`The signature is for ${s.releaseId}, not ${expected.releaseId}`);
  if ((s.tenant ?? null) !== (expected.tenant ?? null)) throw new SignatureRejected(`The signature is for tenant ${s.tenant ?? '(shared)'}, not ${expected.tenant ?? '(shared)'}`);
  const listed = new Map<string, string>((s.files ?? []).map((f: any) => [f.path, f.sha256]));
  for (const [p, c] of Object.entries(files)) {
    if (isSignatureFile(p)) continue;
    if (!listed.has(p)) throw new SignatureRejected(`${p} is not in the signed statement`);
    if (listed.get(p) !== sha256(bytes(c))) throw new SignatureRejected(`${p} does not match its hash in the signed statement`);
  }
  const missing = [...listed.keys()].filter((p) => !(p in files));
  if (missing.length) throw new SignatureRejected(`Signed but not in the package: ${missing.slice(0, 5).join(', ')}`);
  return { kind, keyId: s.keyId, statementSha256: sha256(stBytes), changeSet: s.changeSet ?? null, signedAt: s.signedAt, files: listed.size };
}

/** A P-256 key pair for a test or a first installation: the private key as PKCS#8 PEM, the public key and its trusted-signers entry. */
export function generateSigningKey(): { privateKeyPem: string; publicKeyPem: string; keyId: string } {
  const { privateKey: k, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  return { privateKeyPem: k.export({ type: 'pkcs8', format: 'pem' }).toString(), publicKeyPem: publicKey.export({ type: 'spki', format: 'pem' }).toString(), keyId: keyIdOf(publicKey) };
}

export function trustedSignersJson(keys: { publicKeyPem: string; keyId: string; retiredAt?: string }[], revoked: { keyId: string; revokedAt: string; reason?: string }[] = []): string {
  return JSON.stringify({ kind: 'trusted-signers', keys: keys.map((k) => ({ keyId: k.keyId, publicKey: k.publicKeyPem, ...(k.retiredAt ? { retiredAt: k.retiredAt } : {}) })), revoked }, null, 2) + '\n';
}
