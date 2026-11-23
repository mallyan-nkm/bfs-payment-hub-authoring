import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

// A promotable distribution carries the native assemblies of its schemes (R-09-120). The unit tests build distributions without
// the .NET SDK, so each run makes a synthetic module build of WPS in a folder of its own and names it with TH_NATIVE_MODULES, as
// a studio with no SDK names the build made ahead of time. Its bytes are synthetic: no runtime ever loads them.

export const SYNTHETIC_ASSEMBLY = 'TransactHub.Activities.Wps';

export function syntheticModules(content = 'synthetic WPS native assembly for the studio tests'): string {
  // One folder per content, the same for every test file and run: nothing is left behind per run.
  const bytes = Buffer.from(content);
  const dir = path.join(os.tmpdir(), `th-test-native-${createHash('sha256').update(bytes).digest('hex').slice(0, 16)}`);
  const scope = path.join(dir, 'uae.wps');
  fs.mkdirSync(scope, { recursive: true });
  fs.writeFileSync(path.join(scope, `${SYNTHETIC_ASSEMBLY}.dll`), bytes);
  fs.writeFileSync(path.join(scope, 'native-module.json'), JSON.stringify({
    format: 'th-native-module/1', scope: 'uae.wps', assembly: SYNTHETIC_ASSEMBLY, contract: '1.0.0',
    files: [{ name: `${SYNTHETIC_ASSEMBLY}.dll`, sha256: `sha256:${createHash('sha256').update(bytes).digest('hex')}` }],
  }));
  return dir;
}

process.env.TH_NATIVE_MODULES ??= syntheticModules();
