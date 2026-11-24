import * as fs from 'node:fs';
import { afterAll } from 'vitest';
import { MODULE_SIGNERS, syntheticModuleFolder } from './synthetic-modules.js';

// The unit tests' module builds (test/support/synthetic-modules.ts): each test file gets a folder of its own, named by TH_NATIVE_MODULES.
export { CONTRACT, MODULE_KEY_ID, MODULE_SIGNERS, SYNTHETIC_ASSEMBLY, SYNTHETIC_PLATFORM_ASSEMBLY, writeModuleFolder } from './synthetic-modules.js';

/** A synthetic module build in a folder of this test file's, removed when the file's tests end. */
export function syntheticModules(...a: Parameters<typeof syntheticModuleFolder>): string {
  const dir = syntheticModuleFolder(...a);
  made.push(dir);
  return dir;
}

const made: string[] = [];
afterAll(() => { for (const d of made.splice(0)) fs.rmSync(d, { recursive: true, force: true }); });

// Each test file gets its own folder (the one before it is removed with its file); a folder the caller named is kept.
const outer = process.env.TH_TEST_NATIVE_MODULES_OUTER ?? (process.env.TH_NATIVE_MODULES ? '1' : '0');
process.env.TH_TEST_NATIVE_MODULES_OUTER = outer;
process.env.TH_NATIVE_MODULE_SIGNERS = MODULE_SIGNERS;
if (outer === '0') process.env.TH_NATIVE_MODULES = syntheticModules();
