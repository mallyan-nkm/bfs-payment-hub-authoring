import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { imageReferences, moduleSignature, readModuleFolder, sourceSha256 } from '../../src/export/native-modules.js';
import { generateSigningKey } from '../../src/export/signing.js';

// R-09-127 (Issue I-223): runtime/tools/native-module/build.sh signs the module build it makes ahead of time, with a key from the
// environment only, and the studio verifies that signature when it takes the folder. Each build here is of the real code-behind
// projects, with a key made for the test and held in an environment variable; nothing of the key is written to a file.

const REPO = path.resolve(__dirname, '..', '..', '..');
const BUILD = path.join(REPO, 'runtime', 'tools', 'native-module', 'build.sh');

describe('a module build made ahead of time, signed by build.sh (R-09-127)', () => {
  it('is verified by the studio when signed; refused when tampered or unsigned', () => {
    const out = fs.mkdtempSync(path.join(os.tmpdir(), 'th-build-sh-'));
    const k = generateSigningKey();
    const env = { ...process.env, TH_TEST_MODULE_KEY: k.privateKeyPem, TH_NATIVE_SIGNING_KEY: 'env:TH_TEST_MODULE_KEY', DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };
    try {
      for (const [scope, project] of [['uae.wps', 'runtime/src/TransactHub.Activities.Wps'], ['p', 'runtime/src/TransactHub.Activities.Platform.Native']]) {
        const r = spawnSync('sh', [BUILD, scope, path.join(REPO, project), out], { env, encoding: 'utf8', cwd: REPO });
        expect(r.status, r.stdout + r.stderr).toBe(0);
        expect(r.stdout).toMatch(/signed by key/);
      }
      const signers = spawnSync('sh', [BUILD, '--signers'], { env, encoding: 'utf8' });
      expect(signers.status, signers.stderr).toBe(0);
      const trust = { ...process.env, TH_NATIVE_MODULE_SIGNERS: signers.stdout, TH_NATIVE_MODULES_UNVERIFIED: '' };
      for (const scope of ['uae.wps', 'p']) {
        const dir = path.join(out, scope);
        expect(moduleSignature(dir, scope, trust)).toEqual({ keyId: k.keyId });
        const m = readModuleFolder(dir)!;
        // The source hash build.sh wrote is the studio's; the module references the native contract only (R-09-125).
        expect(m.sourceSha256).toBe(sourceSha256(path.join(REPO, m.project!)));
        expect(m.contract).toBe('2.2.0');
        expect(imageReferences(m)).toEqual([]);
      }
      // Tampered: one byte of the assembly changed.
      const dll = path.join(out, 'uae.wps', 'TransactHub.Activities.Wps.dll');
      const b = fs.readFileSync(dll);
      b[b.length - 1] ^= 0x5a;
      fs.writeFileSync(dll, b);
      expect(() => moduleSignature(path.join(out, 'uae.wps'), 'uae.wps', trust)).toThrow(/does not match its hash in the signed statement/);
      // Unsigned: built with no key.
      const unsignedOut = path.join(out, 'unsigned');
      const r = spawnSync('sh', [BUILD, 'p', path.join(REPO, 'runtime/src/TransactHub.Activities.Platform.Native'), unsignedOut], { env: { ...env, TH_NATIVE_SIGNING_KEY: '' }, encoding: 'utf8', cwd: REPO });
      expect(r.status, r.stderr).toBe(0);
      expect(r.stdout).toMatch(/NOT signed/);
      expect(() => moduleSignature(path.join(unsignedOut, 'p'), 'p', trust)).toThrow(/The package is not signed/);
      // No file of the folder holds the key.
      for (const f of fs.readdirSync(path.join(out, 'p'))) expect(fs.readFileSync(path.join(out, 'p', f), 'latin1')).not.toContain('PRIVATE KEY');
    } finally { fs.rmSync(out, { recursive: true, force: true }); }
  });
});
