import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { createPrivateKey } from 'node:crypto';
import { afterAll } from 'vitest';
import { generateSigningKey, signFiles, trustedSignersJson } from '../../src/export/signing.js';
import { syntheticAssembly } from './synthetic-assembly.js';

// A promotable distribution carries the native assemblies of every pack with native activities, the platform's among them
// (R-09-120, R-09-126). The unit tests build distributions without the .NET SDK, so each run makes a synthetic module build of the
// platform and of WPS in a folder of its own and names it with TH_NATIVE_MODULES, as a studio with no SDK names the build made ahead
// of time. Each assembly is a synthetic PE that references the native contract only (test/support/synthetic-assembly.ts); each
// folder is signed as runtime/tools/native-module/build.sh signs one, by a key made for the run and trusted through
// TH_NATIVE_MODULE_SIGNERS (R-09-127). No runtime ever loads them.

export const SYNTHETIC_ASSEMBLY = 'TransactHub.Activities.Wps';
export const SYNTHETIC_PLATFORM_ASSEMBLY = 'TransactHub.Activities.Platform.Native';
export const CONTRACT = { name: 'TransactHub.Activities.Contract', version: '2.0.0' };

const key = generateSigningKey();
/** The trusted-signers document of the run's module-signing key. */
export const MODULE_SIGNERS = trustedSignersJson([key]);
export const MODULE_KEY_ID = key.keyId;

const sha = (b: Buffer | string) => `sha256:${createHash('sha256').update(b).digest('hex')}`;

/** Writes one module build folder, signed unless `sign` is false. */
export function writeModuleFolder(dir: string, scope: string, assembly: string, bytes: Buffer, sign = true, contract = '2.0.0') {
  fs.mkdirSync(dir, { recursive: true });
  const manifest = {
    format: 'th-native-module/1', scope, assembly, contract, sourceSha256: sha(`synthetic sources of ${scope}`),
    files: [{ name: `${assembly}.dll`, sha256: sha(bytes) }],
  };
  const files: Record<string, string | Buffer> = { [`${assembly}.dll`]: bytes, 'native-module.json': `${JSON.stringify(manifest, null, 2)}\n` };
  const out = sign
    ? signFiles(files, {
      kind: 'native-module', releaseId: `native-module/${scope}`, workflow: '', release: contract, target: 'native', tenant: null, changeSet: null,
      extra: { scope, assembly, contract, sourceSha256: manifest.sourceSha256, assemblies: manifest.files },
    }, createPrivateKey(key.privateKeyPem))
    : files;
  for (const [n, c] of Object.entries(out)) fs.writeFileSync(path.join(dir, n), c);
}

export function syntheticModules(content = 'synthetic WPS native assembly for the studio tests', opts: { sign?: boolean; wpsRefs?: { name: string; version?: string }[] } = {}): string {
  // One folder per content and options in this test file, removed when the file's tests end.
  const tag = createHash('sha256').update(JSON.stringify([content, opts])).digest('hex').slice(0, 16);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), `th-test-native-${tag}-`));
  made.push(dir);
  writeModuleFolder(path.join(dir, 'uae.wps'), 'uae.wps', SYNTHETIC_ASSEMBLY, syntheticAssembly(SYNTHETIC_ASSEMBLY, opts.wpsRefs ?? [CONTRACT, { name: 'System.Runtime', version: '10.0.0' }], content), opts.sign ?? true);
  writeModuleFolder(path.join(dir, 'p'), 'p', SYNTHETIC_PLATFORM_ASSEMBLY, syntheticAssembly(SYNTHETIC_PLATFORM_ASSEMBLY, [CONTRACT, { name: 'System.Runtime', version: '10.0.0' }], 'synthetic platform native assembly'), opts.sign ?? true);
  return dir;
}

const made: string[] = [];
afterAll(() => { for (const d of made.splice(0)) fs.rmSync(d, { recursive: true, force: true }); });

// Each test file gets its own folder (the one before it is removed with its file); a folder the caller named is kept.
const outer = process.env.TH_TEST_NATIVE_MODULES_OUTER ?? (process.env.TH_NATIVE_MODULES ? '1' : '0');
process.env.TH_TEST_NATIVE_MODULES_OUTER = outer;
process.env.TH_NATIVE_MODULE_SIGNERS = MODULE_SIGNERS;
if (outer === '0') process.env.TH_NATIVE_MODULES = syntheticModules();
