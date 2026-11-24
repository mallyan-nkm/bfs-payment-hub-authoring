import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { buildDistribution } from '../src/export/distribution.js';
import { assemblyReferences } from '../src/export/assembly-refs.js';
import { CONTRACT_UNSUPPORTED, contractSupported, IMAGE_REFERENCE, imageReferences, isModuleBuiltin, isNative, isPlatformScope, moduleSignature, nativeModules, NATIVE_CONTRACT, readModuleFolder, sourceSha256 } from '../src/export/native-modules.js';
import { generateSigningKey, parseTrustedSigners, trustedSignersJson, verifyFiles } from '../src/export/signing.js';
import { freshEnv } from './helpers.js';
import { testSigning } from './support/signing-env.js';
import { CONTRACT, MODULE_KEY_ID, syntheticModules, SYNTHETIC_ASSEMBLY, SYNTHETIC_PLATFORM_ASSEMBLY, writeModuleFolder } from './support/native-modules.js';
import { syntheticAssembly } from './support/synthetic-assembly.js';

// A distribution carries the native assemblies of every pack's native activities, the platform's among them (R-09-120, R-09-126),
// each with the SHA-256 of its bytes in the signed bill of materials, built against the native contract it names (R-09-123). A
// module references the contract and no other assembly of TransactHub (R-09-125), and a build made ahead of time is signed
// (R-09-127). docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3.

const sha = (b: Buffer | string) => `sha256:${createHash('sha256').update(b).digest('hex')}`;
const saved = { modules: process.env.TH_NATIVE_MODULES, signers: process.env.TH_NATIVE_MODULE_SIGNERS, unverified: process.env.TH_NATIVE_MODULES_UNVERIFIED, node: process.env.NODE_ENV };
afterEach(() => {
  for (const [k, v] of [['TH_NATIVE_MODULES', saved.modules], ['TH_NATIVE_MODULE_SIGNERS', saved.signers], ['TH_NATIVE_MODULES_UNVERIFIED', saved.unverified], ['NODE_ENV', saved.node]] as const) {
    if (v === undefined) delete process.env[k]; else process.env[k] = v;
  }
});
const scopeOf = (n: string) => (n.startsWith('p.') ? 'p' : n.split('.').slice(0, 2).join('.'));

describe('a distribution\'s native assemblies (R-09-120)', () => {
  it('carries the platform\'s and the scheme\'s module builds, each assembly as base64 with the SHA-256 of its bytes in the signed bill of materials', () => {
    const env = freshEnv();
    const d = buildDistribution(env, { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-1' });
    const native = d.manifest.native;
    expect(native.contract).toBe('2.0.0');
    expect(native.platform).toBeUndefined();
    expect(native.modules.map((m: any) => m.scope)).toEqual(['p', 'uae.wps']);
    const [p, m] = native.modules;
    expect(m.assembly).toBe(SYNTHETIC_ASSEMBLY);
    // Every native activity of the scheme, at the release the distribution carries.
    expect(m.activities).toContain('uae.wps.map.ack-in.v1@1.2.0');
    expect(m.activities.every((a: string) => a.startsWith('uae.wps.'))).toBe(true);
    // The platform's native activities are the platform module's (R-09-126), not the runtime image's.
    expect(p.assembly).toBe(SYNTHETIC_PLATFORM_ASSEMBLY);
    for (const a of ['p.enrich.fx-rate.v1', 'p.enrich.charges.v1', 'p.decide.posting-window.v1', 'p.post.accounting.v2', 'p.enrich.party-register.v1', 'p.protect.encrypt-file.v1', 'p.protect.decrypt-file.v1'])
      expect(p.activities.some((x: string) => x.startsWith(`${a}@`))).toBe(true);
    // Since native contract 2.0.0 the builtin posting is the platform module's too (R-09-264, Issue I-227): the image keeps no copy.
    expect(p.activities.some((x: string) => x.startsWith('p.post.accounting.v1@'))).toBe(true);
    // Each prebuilt folder was verified against the signers the studio trusts (R-09-127).
    expect(m.signature).toEqual({ keyId: MODULE_KEY_ID });
    const f = m.files[0];
    expect(f.path).toBe(`activities/native/uae.wps/${SYNTHETIC_ASSEMBLY}.dll.base64`);
    const bytes = Buffer.from(d.files[f.path], 'base64');
    expect(sha(bytes)).toBe(f.sha256);
    const bom = d.manifest.billOfMaterials.artefacts.find((a: any) => a.path === f.path);
    expect(bom).toMatchObject({ kind: 'native-assembly', scope: 'uae.wps', name: `${SYNTHETIC_ASSEMBLY}.dll`, assemblySha256: f.sha256, sha256: sha(d.files[f.path]) });
    expect(d.manifest.billOfMaterials.artefacts.some((a: any) => a.path === `activities/native/p/${SYNTHETIC_PLATFORM_ASSEMBLY}.dll.base64`)).toBe(true);
    // Signed with the manifest: the statement's bill of materials holds the hash.
    const st = JSON.parse(d.files['release.json']);
    expect(JSON.stringify(st.billOfMaterials)).toContain(f.sha256);
    verifyFiles(d.files, { releaseId: d.releaseId, tenant: 'example-bank', kind: 'distribution' }, parseTrustedSigners(fs.readFileSync(testSigning().trustFile, 'utf8')));
  });

  it('a different build of the same code-behind is another assembly, with another hash', () => {
    const env = freshEnv();
    const a = buildDistribution(env, { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-2' });
    process.env.TH_NATIVE_MODULES = syntheticModules('another build of the synthetic WPS native assembly');
    const b = buildDistribution(env, { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-3' });
    const wps = (d: any) => d.manifest.native.modules.find((m: any) => m.scope === 'uae.wps');
    expect(wps(a).files[0].sha256).not.toBe(wps(b).files[0].sha256);
  });

  it('without a build of the code-behind, the distribution says what it does not carry; one with no script-behind is refused', () => {
    delete process.env.TH_NATIVE_MODULES;
    const env = freshEnv();
    // The platform's and WPS's native activities have script-behinds (implementation.native: true): without the build they run as
    // scripts, in a test distribution.
    const t = buildDistribution(env, { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-5', withSimulators: true });
    expect(t.manifest.native).toMatchObject({ modules: [], notCarried: ['p', 'uae.wps'] });
    expect(Object.keys(t.files).some((p) => p.startsWith('activities/native/'))).toBe(false);
    // The builtin posting has no script-behind and the image no copy (R-09-264): a promotable distribution needs the platform's build.
    expect(() => buildDistribution(env, { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-4' })).toThrow(/p has native activities with no script-behind \(p\.post\.accounting\.v1@/);
    // An activity with no script-behind cannot run without its build: a promotable distribution that needs one is refused.
    const only = [{ kind: 'activity', name: 'uae.wps.enrich.synthetic.v1', release: '1.0.0', implementation: { binding: 'native' } }];
    expect(nativeModules(only, scopeOf, true).problems.join()).toMatch(/uae\.wps has native activities with no script-behind \(uae\.wps\.enrich\.synthetic\.v1@1\.0\.0\) .* set TH_NATIVE_MODULES \(R-09-120\)/);
    expect(nativeModules(only, scopeOf, false)).toMatchObject({ problems: [], section: { notCarried: ['uae.wps'] } });
  });

  it('a module build whose assembly does not match its own hash is refused', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'th-test-tampered-'));
    fs.cpSync(syntheticModules(), dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'uae.wps', `${SYNTHETIC_ASSEMBLY}.dll`), 'tampered');
    process.env.TH_NATIVE_MODULES = dir;
    try {
      expect(() => buildDistribution(freshEnv(), { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-6' })).toThrow(/does not match its SHA-256/);
    } finally { fs.rmSync(dir, { recursive: true, force: true }); }
  });

  it('a project\'s source hash follows its sources, as sha256sum lists them in byte order', () => {
    expect(isPlatformScope('p')).toBe(true);
    expect(isPlatformScope('uae.wps')).toBe(false);
    expect(isNative({ kind: 'activity', implementation: { binding: 'script', native: true } })).toBe(true);
    expect(isNative({ kind: 'activity', implementation: { binding: 'script' } })).toBe(false);
    expect(isModuleBuiltin({ kind: 'activity', name: 'p.post.accounting.v1', implementation: { binding: 'builtin', service: 'accounting' } }, 'p')).toBe(true);
    expect(isModuleBuiltin({ kind: 'activity', name: 'p.post.reverse.v1', implementation: { binding: 'builtin', service: 'accounting' } }, 'p')).toBe(false);
    const p = fs.mkdtempSync(path.join(os.tmpdir(), 'th-src-'));
    fs.writeFileSync(path.join(p, 'M.csproj'), '<Project/>');
    fs.writeFileSync(path.join(p, 'A.cs'), 'class A { string N = "build A"; }');
    fs.mkdirSync(path.join(p, 'Sub'));
    fs.writeFileSync(path.join(p, 'Sub', 'b.cs'), 'class B {}');
    const one = sourceSha256(p);
    // build.sh's way: find, sort in byte order, sha256sum each, sha256sum the list.
    const h = (b: Buffer | string) => createHash('sha256').update(b).digest('hex');
    const list = ['A.cs', 'M.csproj', 'Sub/b.cs'].map((f) => `${h(fs.readFileSync(path.join(p, f)))}  ${f}\n`).join('');
    expect(one).toBe(`sha256:${h(list)}`);
    fs.mkdirSync(path.join(p, 'obj'));
    fs.writeFileSync(path.join(p, 'obj', 'x.cs'), 'ignored');
    expect(sourceSha256(p)).toBe(one);
    fs.writeFileSync(path.join(p, 'A.cs'), 'class A { string N = "build B"; }');
    expect(sourceSha256(p)).not.toBe(one);
    fs.rmSync(p, { recursive: true, force: true });
  });
});

describe('a native module references the native contract only (R-09-125, Issue I-220)', () => {
  it('reads an assembly\'s references from its metadata, as a real .NET assembly holds them', () => {
    const refs = assemblyReferences(syntheticAssembly('M', [CONTRACT, { name: 'TransactHub.Ports', version: '1.0.0' }]));
    expect(refs).toEqual([{ name: 'TransactHub.Activities.Contract', version: '2.0.0' }, { name: 'TransactHub.Ports', version: '1.0.0' }]);
    expect(assemblyReferences(Buffer.from('not an assembly'))).toBeUndefined();
  });

  // Where this machine has the .NET runtime, a real assembly is read too: the reader is not only proved on the test's own writer.
  const shared = ['/usr/lib/dotnet', '/usr/share/dotnet', '/usr/local/share/dotnet'].map((d) => path.join(d, 'shared', 'Microsoft.NETCore.App')).find((d) => fs.existsSync(d));
  it.skipIf(!shared)('reads a real assembly of the .NET runtime', () => {
    const v = fs.readdirSync(shared!).sort().pop()!;
    const refs = assemblyReferences(fs.readFileSync(path.join(shared!, v, 'System.Text.Json.dll')));
    expect(refs?.map((r) => r.name)).toContain('System.Runtime');
  });

  it('a module whose assembly references a platform assembly is refused with its code; the WPS module that references the contract only passes', () => {
    process.env.TH_NATIVE_MODULES = syntheticModules('a WPS build that reaches into the image', { wpsRefs: [CONTRACT, { name: 'TransactHub.Activities.Platform', version: '1.0.0' }, { name: 'TransactHub.Ports', version: '1.0.0' }] });
    expect(() => buildDistribution(freshEnv(), { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-7' }))
      .toThrow(new RegExp(`${IMAGE_REFERENCE.replace('.', '\\.')}: ${SYNTHETIC_ASSEMBLY}\\.dll of uae\\.wps references TransactHub\\.Activities\\.Platform`));
    const good = readModuleFolder(path.join(syntheticModules(), 'uae.wps'))!;
    expect(imageReferences(good)).toEqual([]);
    // An assembly that does not reference the contract is no module.
    const bare = { ...good, files: [{ ...good.files[0], content: syntheticAssembly(SYNTHETIC_ASSEMBLY, [{ name: 'System.Runtime' }]) }] };
    expect(imageReferences(bare).join()).toMatch(/does not reference the native contract/);
  });
});

describe('native contract 2.0.0 (R-09-262, Issues I-226, I-224)', () => {
  it('the studio builds for contract 2.0.0 and refuses a module build made against contract 1.x, with its code', () => {
    expect(NATIVE_CONTRACT).toBe('2.0.0');
    expect(contractSupported('2.0.0', '2.0.0')).toBe(true);
    for (const v of ['1.0.0', '1.1.0', '2.1.0', '3.0.0', undefined, 'x']) expect(contractSupported(v, '2.0.0')).toBe(false);
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'th-test-contract1-'));
    try {
      // A build made ahead of time against contract 1.1.0, signed by a trusted key: still refused, to be rebuilt.
      writeModuleFolder(path.join(root, 'p'), 'p', SYNTHETIC_PLATFORM_ASSEMBLY, syntheticAssembly(SYNTHETIC_PLATFORM_ASSEMBLY, [{ name: CONTRACT.name, version: '2.0.0' }]));
      writeModuleFolder(path.join(root, 'uae.wps'), 'uae.wps', SYNTHETIC_ASSEMBLY, syntheticAssembly(SYNTHETIC_ASSEMBLY, [{ name: CONTRACT.name, version: '1.1.0' }]), true, '1.1.0');
      process.env.TH_NATIVE_MODULES = root;
      expect(() => buildDistribution(freshEnv(), { bank: 'example-bank', engine: 'temporal', id: '2026.11.24-31' }))
        .toThrow(new RegExp(`${CONTRACT_UNSUPPORTED.replace('.', '\\.')}: the module build of uae\\.wps was built against native contract 1\\.1\\.0; .*rebuild it against 2\\.0\\.0`));
    } finally { fs.rmSync(root, { recursive: true, force: true }); }
  });
});

describe('a module build made ahead of time is signed (R-09-127, Issue I-223)', () => {
  it('a folder signed by a trusted key is taken; a tampered one and an unsigned one are refused', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'th-test-signed-'));
    try {
      const good = path.join(root, 'good', 'uae.wps');
      writeModuleFolder(good, 'uae.wps', SYNTHETIC_ASSEMBLY, syntheticAssembly(SYNTHETIC_ASSEMBLY, [CONTRACT]));
      expect(moduleSignature(good, 'uae.wps')).toEqual({ keyId: MODULE_KEY_ID });

      // Tampered: the assembly and its hash in native-module.json both changed, so only the signature tells.
      const bad = path.join(root, 'tampered', 'uae.wps');
      fs.cpSync(good, bad, { recursive: true });
      const other = syntheticAssembly(SYNTHETIC_ASSEMBLY, [CONTRACT], 'another build');
      fs.writeFileSync(path.join(bad, `${SYNTHETIC_ASSEMBLY}.dll`), other);
      const m = JSON.parse(fs.readFileSync(path.join(bad, 'native-module.json'), 'utf8'));
      m.files[0].sha256 = sha(other);
      fs.writeFileSync(path.join(bad, 'native-module.json'), `${JSON.stringify(m, null, 2)}\n`);
      expect(() => moduleSignature(bad, 'uae.wps')).toThrow(/module build of uae\.wps .* is refused: .*does not match its hash in the signed statement/);
      // Signed for another scope.
      expect(() => moduleSignature(good, 'p')).toThrow(/is for native-module\/uae\.wps, not native-module\/p/);

      // Unsigned.
      const unsigned = path.join(root, 'unsigned', 'uae.wps');
      writeModuleFolder(unsigned, 'uae.wps', SYNTHETIC_ASSEMBLY, syntheticAssembly(SYNTHETIC_ASSEMBLY, [CONTRACT]), false);
      expect(() => moduleSignature(unsigned, 'uae.wps')).toThrow(/is refused: The package is not signed/);
      // Through the distribution build too.
      process.env.TH_NATIVE_MODULES = path.join(root, 'unsigned');
      expect(() => buildDistribution(freshEnv(), { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-8' })).toThrow(/module build of uae\.wps .* is refused/);

      // Signed by a key the studio does not trust.
      process.env.TH_NATIVE_MODULE_SIGNERS = trustedSignersJson([generateSigningKey()]);
      expect(() => moduleSignature(good, 'uae.wps')).toThrow(/which this deployment does not trust/);

      // The test and development mode takes it, and says so; never in production.
      process.env.TH_NATIVE_MODULES_UNVERIFIED = 'test';
      expect(moduleSignature(unsigned, 'uae.wps')).toMatchObject({ unverified: 'test' });
      const d = buildDistribution(freshEnv(), { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-9', withSimulators: true });
      expect(d.manifest.native.modules.find((x: any) => x.scope === 'uae.wps').signature).toMatchObject({ unverified: 'test' });
      process.env.NODE_ENV = 'production';
      expect(() => moduleSignature(unsigned, 'uae.wps')).toThrow(/TH_NATIVE_MODULES_UNVERIFIED=test is refused when NODE_ENV is production/);
    } finally { fs.rmSync(root, { recursive: true, force: true }); }
  });
});
