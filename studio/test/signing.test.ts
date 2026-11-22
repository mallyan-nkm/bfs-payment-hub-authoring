import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { inspect } from 'node:util';
import { afterAll, describe, expect, it } from 'vitest';
import { exportPackage, exportZip, signPackage, signedZip } from '../src/export/exporter.js';
import { claimsOf, generateSigningKey, keyIdOf, parseTrustedSigners, signFiles, signingKey, trustedSignersJson, verifyFiles, SignatureRejected } from '../src/export/signing.js';
import { deploy, verifyPackageDir } from '../src/deploy/deploy.js';
import { unzip } from '../src/export/zip.js';
import { resolveSecret, resolveSecretSync, SecretUnavailable, SecretValue } from '../src/secrets/secrets.js';
import { literalSecretsIn, literalSecretsInText } from '../src/secrets/scan.js';
import { checkAll } from '../src/checks/checks.js';
import { dispatch } from '../src/server/api.js';
import { parseYaml } from '../src/core/yaml.js';
import { toYaml } from '../src/core/yaml.js';
import { freshEnv } from './helpers.js';
import { fake } from './support/content.js';
import { testSigning } from './support/signing-env.js';
import { createPrivateKey } from 'node:crypto';

// Secrets by reference (R-06-22, R-06-23) and the signature of every released package (R-09-23 to R-09-26).
// Every key here is made by the test, in a folder of its own.

const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-signing-test-'));
afterAll(() => fs.rmSync(work, { recursive: true, force: true }));
const synthetic = () => `synthetic-${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;
const call = (env: any, method: string, p: string, body: any = {}, query: any = {}) => dispatch(env, { method, path: p, query, body });

const CLAIMS = { releaseId: 'p.wf.corporate.v1/temporal@1.0.0', workflow: 'p.wf.corporate.v1', release: '1.0.0', target: 'temporal', tenant: 'example-bank', changeSet: 'CS-2026-11-22-001' };
const EXPECTED: { releaseId: string; tenant: string | null; kind?: 'workflow-package' | 'distribution' | 'configuration' } = { releaseId: CLAIMS.releaseId, tenant: CLAIMS.tenant };
const FILES = () => ({ 'manifest.yaml': 'kind: export\n', 'README.md': '# Export package\n', 'workflows/p.wf.corporate.v1@1.0.0.yaml': 'kind: workflow\n' }) as Record<string, string>;
const keyOf = (k: ReturnType<typeof generateSigningKey>) => createPrivateKey(k.privateKeyPem);

describe('secrets are read by reference only (R-06-22)', () => {
  it('reads env: and file: references, and says what is missing without a value', () => {
    const value = synthetic();
    expect(resolveSecretSync('env:TH_T', { TH_T: value }).reveal()).toBe(value);
    expect(() => resolveSecretSync('env:TH_MISSING', {})).toThrow('Secret env:TH_MISSING is not set');
    const f = path.join(work, 'token');
    fs.writeFileSync(f, value + '\n');
    expect(resolveSecretSync(`file:${f}`, { TH_SECRET_FILE_ROOTS: work }).reveal()).toBe(value);
    expect(() => resolveSecretSync('file:/etc/hostname', { TH_SECRET_FILE_ROOTS: work })).toThrow('outside the folders');
    expect(() => resolveSecretSync(`file:${work}/../x`, {})).toThrow("without '..'");
    expect(() => resolveSecretSync('akv:vault/secret', {})).toThrow('I-73');
  });

  it('never shows a value: not in an error, a log line, JSON or inspect', () => {
    const value = synthetic();
    let e: any;
    try {
      resolveSecretSync(value, {});
    } catch (x) {
      e = x;
    }
    expect(e).toBeInstanceOf(SecretUnavailable);
    expect(e.message).not.toContain(value);
    expect(e.message).toContain(`${value.length} characters`);
    const s = new SecretValue(value);
    expect(`${s}`).toBe('[secret]');
    expect(JSON.stringify({ s })).toBe('{"s":"[secret]"}');
    expect(inspect({ s })).not.toContain(value);
  });

  it('reads a field of a Vault KV secret through a token held by reference, and says why without a value when it cannot', async () => {
    const value = synthetic();
    const token = synthetic();
    const fetcher = (async (url: any, init: any) => {
      if (init.headers['X-Vault-Token'] !== token) return new Response('{"errors":["permission denied"]}', { status: 403 });
      return String(url).endsWith('/v1/secret/data/transacthub/test') ? Response.json({ data: { data: { key: value } } }) : new Response('{}', { status: 404 });
    }) as typeof fetch;
    const env = { TH_VAULT_ADDR: 'http://vault.invalid:8200', TH_VAULT_TOKEN: 'env:TH_VT', TH_VT: token };
    expect((await resolveSecret('vault:secret/transacthub/test#key', env, fetcher)).reveal()).toBe(value);
    await expect(resolveSecret('vault:secret/transacthub/test#other', env, fetcher)).rejects.toThrow('has no field other');
    await expect(resolveSecret('vault:secret/transacthub/none#key', env, fetcher)).rejects.toThrow('Vault answered 404');
    const wrong = await resolveSecret('vault:secret/transacthub/test#key', { ...env, TH_VT: 'wrong' }, fetcher).catch((x) => x);
    expect(String(wrong)).toContain('Vault answered 403');
    expect(String(wrong)).not.toContain(token);
    await expect(resolveSecret('vault:secret/x#y', {})).rejects.toThrow('TH_VAULT_ADDR');
  });
});

describe('no literal secret in a binding, a configuration or a package (R-06-23, L37)', () => {
  it('finds a value under a secret key, a secretRef that is not a reference and a private key, and nothing else', () => {
    expect(literalSecretsIn({ settings: { secretRef: 'env:TH_X', password: 'file:/run/secrets/x', tokenTtl: 'PT1H', title: 'token' } }, 'a.yaml')).toEqual([]);
    const found = literalSecretsIn({ bindings: [{ settings: { secretRef: 'hunter2', gatewayPassword: 'not-a-reference', apiKey: 12345 } }] }, 'b.yaml');
    expect(found.map((f) => f.at)).toEqual(['bindings[0].settings.secretRef', 'bindings[0].settings.gatewayPassword', 'bindings[0].settings.apiKey']);
    expect(found.map((f) => f.why).join(' ')).not.toContain('hunter2');
    const pem = generateSigningKey().privateKeyPem;
    expect(literalSecretsInText(`kind: x\nnote: |\n  ${pem.split('\n').join('\n  ')}\n`, 'c.yaml')[0].why).toBe('is a private key');
    expect(literalSecretsInText(pem, 'scripts/x.js')[0].why).toBe('holds a private key');
  });

  it('the studio check finds a literal secret a bank put in its binding on the branch', () => {
    const env = freshEnv();
    expect(checkAll(env).filter((i) => i.code === 'L37')).toEqual([]);
    env.store.content!.files.set('banks/example-bank/core-banking-gateway.yaml', toYaml({ kind: 'port-bindings', tenant: 'example-bank', bindings: [{ port: 'p.port.core-banking.v1', adapter: 'gateway-rest', validFrom: '2027-01-01', settings: { secretRef: 'literal-value', password: 'also-literal' } }] }));
    const l37 = checkAll(env).filter((i) => i.code === 'L37');
    expect(l37.map((i) => i.step)).toEqual(['bindings[0].settings.secretRef', 'bindings[0].settings.password']);
    expect(l37.every((i) => i.severity === 'error')).toBe(true);
  });
});

describe('every released package is signed, and only a verified one is taken (R-09-23 to R-09-26)', () => {
  const k = generateSigningKey();
  const trust = parseTrustedSigners(trustedSignersJson([k]));
  const signed = (files = FILES(), key = keyOf(k), at = new Date('2026-11-22T08:00:00Z')) => signFiles(files, CLAIMS, key, at) as Record<string, string>;
  const refusal = (files: Record<string, string | Buffer>, t = trust, expected = EXPECTED) => {
    try {
      verifyFiles(files, expected, t);
    } catch (e: any) {
      expect(e).toBeInstanceOf(SignatureRejected);
      return e.message as string;
    }
    throw new Error('verified');
  };

  it('a signed package verifies, with its key, change set and every file', () => {
    expect(verifyFiles(signed(), EXPECTED, trust)).toMatchObject({ keyId: k.keyId, changeSet: 'CS-2026-11-22-001', files: 3 });
    const st = JSON.parse(signed()['release.json']);
    expect(Object.keys(st)).toEqual(['changeSet', 'files', 'format', 'keyId', 'kind', 'release', 'releaseId', 'signedAt', 'target', 'tenant', 'workflow']);
    expect(st.kind).toBe('workflow-package');
    // Another kind (a distribution) carries its own fields under the same signature, and is not taken as a workflow package.
    const dist = signFiles(FILES(), { ...CLAIMS, kind: 'distribution', releaseId: 'dist/example-bank/2026.11.22-1', extra: { billOfMaterials: [{ pack: 'p', release: '1.4.0' }] } }, keyOf(k)) as Record<string, string>;
    expect(JSON.parse(dist['release.json']).billOfMaterials[0].release).toBe('1.4.0');
    expect(verifyFiles(dist, { releaseId: 'dist/example-bank/2026.11.22-1', tenant: 'example-bank', kind: 'distribution' }, trust).kind).toBe('distribution');
    expect(refusal(dist, trust, { releaseId: 'dist/example-bank/2026.11.22-1', tenant: 'example-bank' })).toContain('is for a distribution, not a workflow-package');
    expect(st.files.map((f: any) => f.path)).toEqual(['README.md', 'manifest.yaml', 'workflows/p.wf.corporate.v1@1.0.0.yaml']);
  });

  it('refuses a tampered, extra or missing file, a missing signature, a wrong key and a revoked key', () => {
    expect(refusal({ ...signed(), 'README.md': '# edited\n' })).toContain('README.md does not match its hash in the signed statement');
    expect(refusal({ ...signed(), 'scripts/smuggled.js': 'x' })).toContain('scripts/smuggled.js is not in the signed statement');
    const missing = signed();
    delete missing['README.md'];
    expect(refusal(missing)).toContain('Signed but not in the package: README.md');
    expect(refusal(FILES())).toContain('is not signed');
    const other = generateSigningKey();
    expect(refusal(signed(FILES(), keyOf(other)))).toContain('does not trust');
    expect(refusal({ ...signed(), 'release.sig': signed(FILES(), keyOf(other))['release.sig'] }, parseTrustedSigners(trustedSignersJson([k, other])))).toContain('does not verify');
    expect(refusal(signed(), parseTrustedSigners(trustedSignersJson([k], [{ keyId: k.keyId, revokedAt: '2026-11-22T09:00:00Z', reason: 'test' }])))).toContain('was revoked');
    expect(refusal(signed(), trust, { ...EXPECTED, tenant: null })).toContain('for tenant example-bank, not (shared)');
    expect(refusal(signed(), trust, { ...EXPECTED, releaseId: 'p.wf.corporate.v1/temporal@1.0.1' })).toContain('not p.wf.corporate.v1/temporal@1.0.1');
  });

  it('rotates keys: the new key is added, the old one retires and verifies only what it signed before', () => {
    const next = generateSigningKey();
    const t = parseTrustedSigners(trustedSignersJson([{ ...k, retiredAt: '2026-12-01T00:00:00Z' }, next]));
    expect(verifyFiles(signed(FILES(), keyOf(k), new Date('2026-11-30T00:00:00Z')), EXPECTED, t).keyId).toBe(k.keyId);
    expect(refusal(signed(FILES(), keyOf(k), new Date('2026-12-02T00:00:00Z')), t)).toContain('which was retired');
    expect(verifyFiles(signed(FILES(), keyOf(next), new Date('2026-12-02T00:00:00Z')), EXPECTED, t).keyId).toBe(next.keyId);
    const lie = JSON.parse(trustedSignersJson([next]));
    lie.keys[0].keyId = k.keyId;
    expect(() => parseTrustedSigners(JSON.stringify(lie))).toThrow('is not the id of its public key');
  });

  it('checks with openssl as a detached blob signature over release.json', () => {
    if (spawnSync('openssl', ['version']).status !== 0) return;
    const s = signed();
    const dir = fs.mkdtempSync(path.join(work, 'openssl-'));
    fs.writeFileSync(path.join(dir, 'release.json'), s['release.json']);
    fs.writeFileSync(path.join(dir, 'release.der'), Buffer.from(s['release.sig'].trim(), 'base64'));
    fs.writeFileSync(path.join(dir, 'signer.pem'), k.publicKeyPem);
    const r = spawnSync('openssl', ['dgst', '-sha256', '-verify', 'signer.pem', '-signature', 'release.der', 'release.json'], { cwd: dir, encoding: 'utf8' });
    expect(r.stdout).toContain('Verified OK');
  });

  it('the studio signs an export with the key TH_SIGNING_KEY names, and refuses without one or with a literal key', () => {
    const env = freshEnv();
    const pkg = exportPackage(env, { workflows: ['p.wf.commercial.v1'], target: 'temporal' });
    const s = signPackage(pkg);
    expect(Object.keys(s.files)).toEqual(expect.arrayContaining(['release.json', 'release.sig']));
    expect(verifyFiles(s.files, { releaseId: claimsOf(pkg.manifest).releaseId, tenant: null }, parseTrustedSigners(fs.readFileSync(testSigning().trustFile, 'utf8')))).toMatchObject({ keyId: testSigning().keyId });
    expect(JSON.parse(s.files['release.json']).releaseId).toBe(`p.wf.commercial.v1/temporal@${pkg.manifest.flows.find((f: any) => f.name === 'p.wf.commercial.v1').release}`);
    expect(() => signingKey({})).toThrow('Set TH_SIGNING_KEY');
    expect(() => signingKey({ TH_SIGNING_KEY: generateSigningKey().privateKeyPem })).toThrow('never the key itself');
    expect(() => signingKey({ TH_SIGNING_KEY: 'env:TH_NOT_SET' })).toThrow('could not be read: Secret env:TH_NOT_SET is not set');
    expect(keyIdOf(signingKey())).toBe(testSigning().keyId);
  });

  it('a package carrying a literal secret is not promotable and is not signed', () => {
    const env = freshEnv();
    const pkg = exportPackage(env, { workflows: ['p.wf.commercial.v1'], target: 'temporal' });
    const leaky = { ...pkg, files: { ...pkg.files, 'configuration/x.yaml': 'settings:\n  password: literal\n' }, issues: [...pkg.issues, { code: 'L37', severity: 'error' as const, key: 'configuration/x.yaml', message: 'configuration/x.yaml at settings.password holds a value' }] };
    expect(() => signPackage(leaky)).toThrow('carries a literal secret and is not signed');
  });

  it('the download is signed, and the same request gives the same package apart from its signature', async () => {
    const env = freshEnv();
    const a = await call(env, 'GET', '/api/export/download', {}, { workflows: 'p.wf.commercial.v1', target: 'temporal' });
    const b = await call(env, 'GET', '/api/export/download', {}, { workflows: 'p.wf.commercial.v1', target: 'temporal' });
    expect(a.status).toBe(200);
    const files = (z: Buffer) => Object.fromEntries(unzip(z).map((e) => [e.name, e.content.toString('utf8')]));
    const fa = files(a.body as Buffer);
    const fb = files(b.body as Buffer);
    const unsigned = (f: Record<string, string>) => Object.fromEntries(Object.entries(f).filter(([k]) => k !== 'release.sig' && k !== 'release.json'));
    expect(unsigned(fa)).toEqual(unsigned(fb));
    expect(JSON.parse(fa['release.json']).files).toEqual(JSON.parse(fb['release.json']).files);
  });

  it('publishing a workflow release puts its signed package on the branch, by the change set that publishes it; without a key nothing is published', async () => {
    const env = freshEnv();
    const wf = structuredClone(env.store.get('p.wf.commercial.v1')!.doc);
    wf.release = '1.0.1';
    wf.title = `${wf.title} (signed release test)`;
    const d = await call(env, 'POST', '/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: wf });
    expect(d.status, JSON.stringify(d.body).slice(0, 400)).toBe(200);
    const key = d.body.draft.key;
    const id = env.store.entries.get(key)!.changeSet!;
    // Without the studio's key, the checker's approval publishes nothing and says why.
    const saved = process.env.TH_SIGNING_KEY;
    delete process.env.TH_SIGNING_KEY;
    try {
      const refused = await call(env, 'POST', '/api/publish', { tenant: 'owner', key, checker: 'shared.checker' });
      expect(refused.status).not.toBe(200);
      expect(JSON.stringify(refused.body)).toContain('TH_SIGNING_KEY');
    } finally {
      process.env.TH_SIGNING_KEY = saved;
    }
    const dir = 'content-configurations/releases/p/p.wf.commercial.v1/1.0.1/temporal';
    expect(Object.keys(fake().files(env.store.content!.repository, 'main')).filter((p) => p.startsWith(dir))).toEqual([]);
    const pub = await call(env, 'POST', '/api/publish', { tenant: 'owner', key, checker: 'shared.checker' });
    expect(pub.status, JSON.stringify(pub.body).slice(0, 400)).toBe(200);
    const branch = fake().files(env.store.content!.repository, 'main');
    const pkgFiles = Object.fromEntries(Object.entries(branch).filter(([p]) => p.startsWith(dir + '/') && !p.endsWith('/package.sha256')).map(([p, t]) => [p.slice(dir.length + 1), t]));
    expect(Object.keys(pkgFiles)).toEqual(expect.arrayContaining(['manifest.yaml', 'release.json', 'release.sig']));
    const v = verifyFiles(pkgFiles, { releaseId: 'p.wf.commercial.v1/temporal@1.0.1', tenant: null }, parseTrustedSigners(fs.readFileSync(testSigning().trustFile, 'utf8')));
    expect(v.changeSet).toBe(id);
    // The version is an annotated tag on the publishing commit, never moved (R-09-30): what a runtime's package source lists.
    const repo = fake().repo(env.store.content!.repository);
    const tagSha = repo.tags.get('release/p.wf.commercial.v1/1.0.1/temporal');
    expect(tagSha).toBeDefined();
    const tagObj: any = fake().objects.get(tagSha!);
    expect(tagObj).toMatchObject({ type: 'tag', objectType: 'commit', object: pub.body.git.commit });
    expect(tagObj.message).toContain(`Change set: ${id}`);
    expect(tagObj.message).toContain(`Signed by key: ${testSigning().keyId}`);
    expect(pub.body.git.tags).toEqual(['release/p.wf.commercial.v1/1.0.1/temporal']);
    expect(parseYaml(pkgFiles['manifest.yaml']).flows.find((f: any) => f.name === 'p.wf.commercial.v1').release).toBe('1.0.1');
  });

  it('the deploy runner refuses an unsigned or tampered package before any worker starts', async () => {
    const env = freshEnv();
    const pkg = exportPackage(env, { workflows: ['uae.wps.wf.salary-file-out.v1'], target: 'dtfx', withSimulators: true });
    const unsignedZip = path.join(work, 'unsigned.zip');
    fs.writeFileSync(unsignedZip, exportZip(pkg));
    const req = (p: string) => deploy({ packagePath: p, workflow: 'uae.wps.wf.salary-file-out.v1', studioRoot: env.studioRoot, environment: { evaluationDate: '2026-11-17' } });
    await expect(req(unsignedZip)).rejects.toThrow('The package is not deployed: The package is not signed');
    const dir = path.join(work, 'tampered');
    for (const e of unzip(signedZip(pkg))) {
      fs.mkdirSync(path.dirname(path.join(dir, e.name)), { recursive: true });
      fs.writeFileSync(path.join(dir, e.name), e.content);
    }
    expect((await verifyPackageDir(dir)).keyId).toBe(testSigning().keyId);
    const wfFile = fs.readdirSync(path.join(dir, 'workflows'))[0];
    fs.appendFileSync(path.join(dir, 'workflows', wfFile), '# edited after release\n');
    await expect(req(dir)).rejects.toThrow(`workflows/${wfFile} does not match its hash in the signed statement`);
    // Trust withdrawn: the same package, untouched, is refused once its key is revoked.
    const clean = path.join(work, 'clean.zip');
    fs.writeFileSync(clean, signedZip(pkg));
    const revokedTrust = path.join(work, 'revoked.json');
    fs.writeFileSync(revokedTrust, trustedSignersJson([], [{ keyId: testSigning().keyId, revokedAt: '2026-11-22T00:00:00Z' }]));
    const saved = process.env.TH_TRUSTED_SIGNERS;
    process.env.TH_TRUSTED_SIGNERS = `file:${revokedTrust}`;
    try {
      await expect(req(clean)).rejects.toThrow('was revoked');
    } finally {
      process.env.TH_TRUSTED_SIGNERS = saved;
    }
  }, 120000);
});
