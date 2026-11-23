import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { buildDistribution } from '../src/export/distribution.js';
import { isNative, isPlatformScope, sourceSha256 } from '../src/export/native-modules.js';
import { parseTrustedSigners, verifyFiles } from '../src/export/signing.js';
import { freshEnv } from './helpers.js';
import { testSigning } from './support/signing-env.js';
import { syntheticModules, SYNTHETIC_ASSEMBLY } from './support/native-modules.js';

// A distribution carries the native assemblies of its schemes' native activities (R-09-120), each with the SHA-256 of its bytes in
// the signed bill of materials, built against the native contract it names (R-09-123); the platform's native code stays in the
// runtime image. docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3.

const sha = (b: Buffer | string) => `sha256:${createHash('sha256').update(b).digest('hex')}`;
const before = process.env.TH_NATIVE_MODULES;
afterEach(() => { process.env.TH_NATIVE_MODULES = before; });

describe('a distribution\'s native assemblies (R-09-120)', () => {
  it('carries the scheme\'s module build, each assembly as base64 with the SHA-256 of its bytes in the signed bill of materials', () => {
    const env = freshEnv();
    const d = buildDistribution(env, { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-1' });
    const native = d.manifest.native;
    expect(native).toMatchObject({ contract: '1.0.0', platform: 'runtime-image' });
    expect(native.modules.map((m: any) => m.scope)).toEqual(['uae.wps']);
    const m = native.modules[0];
    expect(m.assembly).toBe(SYNTHETIC_ASSEMBLY);
    // Every native activity of the scheme, at the release the distribution carries; none of the platform's.
    expect(m.activities).toContain('uae.wps.map.ack-in.v1@1.2.0');
    expect(m.activities.every((a: string) => a.startsWith('uae.wps.'))).toBe(true);
    const f = m.files[0];
    expect(f.path).toBe(`activities/native/uae.wps/${SYNTHETIC_ASSEMBLY}.dll.base64`);
    const bytes = Buffer.from(d.files[f.path], 'base64');
    expect(sha(bytes)).toBe(f.sha256);
    const bom = d.manifest.billOfMaterials.artefacts.find((a: any) => a.path === f.path);
    expect(bom).toMatchObject({ kind: 'native-assembly', scope: 'uae.wps', name: `${SYNTHETIC_ASSEMBLY}.dll`, assemblySha256: f.sha256, sha256: sha(d.files[f.path]) });
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
    expect(a.manifest.native.modules[0].files[0].sha256).not.toBe(b.manifest.native.modules[0].files[0].sha256);
  });

  it('without a build of the scheme\'s code-behind, a promotable distribution is refused and a test distribution says what it does not carry', () => {
    delete process.env.TH_NATIVE_MODULES;
    const env = freshEnv();
    expect(() => buildDistribution(env, { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-4' })).toThrow(/uae\.wps has native activities .* set TH_NATIVE_MODULES \(R-09-120\)/);
    const t = buildDistribution(env, { bank: 'example-bank', engine: 'temporal', id: '2026.11.23-5', withSimulators: true });
    expect(t.manifest.native).toMatchObject({ modules: [], notCarried: ['uae.wps'] });
    expect(Object.keys(t.files).some((p) => p.startsWith('activities/native/'))).toBe(false);
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

  it('the platform\'s native activities are the runtime image\'s; a project\'s source hash follows its sources', () => {
    expect(isPlatformScope('p')).toBe(true);
    expect(isPlatformScope('uae.p')).toBe(true);
    expect(isPlatformScope('uae.wps')).toBe(false);
    expect(isNative({ kind: 'activity', implementation: { binding: 'script', native: true } })).toBe(true);
    expect(isNative({ kind: 'activity', implementation: { binding: 'script' } })).toBe(false);
    const p = fs.mkdtempSync(path.join(os.tmpdir(), 'th-src-'));
    fs.writeFileSync(path.join(p, 'M.csproj'), '<Project/>');
    fs.writeFileSync(path.join(p, 'A.cs'), 'class A { string N = "build A"; }');
    const one = sourceSha256(p);
    fs.mkdirSync(path.join(p, 'obj'));
    fs.writeFileSync(path.join(p, 'obj', 'x.cs'), 'ignored');
    expect(sourceSha256(p)).toBe(one);
    fs.writeFileSync(path.join(p, 'A.cs'), 'class A { string N = "build B"; }');
    expect(sourceSha256(p)).not.toBe(one);
    fs.rmSync(p, { recursive: true, force: true });
  });
});
