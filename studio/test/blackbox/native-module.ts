import * as fs from 'node:fs';
import * as path from 'node:path';
import { buildModule, nativeProject } from '../../src/export/native-modules.js';

// The runtime image carries no native code-behind (R-09-122, R-09-126): a payment's distribution brings the platform's and the
// schemes'. A black-box test that runs native activities on a native host outside any distribution gives the host the platform's
// and WPS's module builds as its development modules (TH_NATIVE_DEV_MODULES), built from their code-behind projects as the studio
// builds them for a distribution.

/** The folders of the platform's and the WPS module builds (native-module.json and its assembly), for TH_NATIVE_DEV_MODULES. */
export function devModules(): string {
  return ['p', 'uae.wps'].map((s) => buildModule(s, nativeProject(s)!).dir!).join(';');
}

/**
 * A copy of the WPS code-behind project whose ACK mapping writes `marker` into status.additionalInformation: a scheme patch release
 * of a WPS native activity, as two distributions would carry two builds of it. `mark` writes another marker into the same copy.
 */
export function wpsCopy(work: string, marker: string): { dir: string; mark: (m: string) => void } {
  const src = nativeProject('uae.wps')!;
  const runtimeSrc = path.dirname(src);
  const dir = path.join(work, 'TransactHub.Activities.Wps');
  const copy = (from: string, to: string) => {
    for (const e of fs.readdirSync(from, { withFileTypes: true })) {
      if (e.name === 'bin' || e.name === 'obj') continue;
      const a = path.join(from, e.name);
      const b = path.join(to, e.name);
      if (e.isDirectory()) { fs.mkdirSync(b, { recursive: true }); copy(a, b); continue; }
      let text = fs.readFileSync(a, 'utf8');
      if (e.name.endsWith('.csproj')) text = text.replace(/Include="\.\.\\/g, `Include="${runtimeSrc}${path.sep}`).replace(/\\/g, path.sep);
      fs.writeFileSync(b, text);
    }
  };
  fs.mkdirSync(dir, { recursive: true });
  copy(src, dir);
  const file = path.join(dir, 'Native', 'InwardActivities.cs');
  const original = fs.readFileSync(file, 'utf8');
  const mark = (m: string) => fs.writeFileSync(file, original.replaceAll('new JsonObject { ["code"] = "ACCP" }', `new JsonObject { ["code"] = "ACCP", ["additionalInformation"] = "${m}" }`));
  mark(marker);
  return { dir, mark };
}
