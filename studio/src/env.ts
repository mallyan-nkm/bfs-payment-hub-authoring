import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Store } from './core/store.js';
import { SchemaValidator } from './core/schema.js';
import { ScriptRegistry } from './interp/sandbox.js';
import type { RunnerEnv } from './testing/runner.js';

// Where things are. The spec repository sits beside the studio, at ../spec.
const here = path.dirname(fileURLToPath(import.meta.url));
export const STUDIO_ROOT = path.resolve(here, '..');
export const SPEC_ROOT = process.env.TH_SPEC_ROOT ?? path.resolve(STUDIO_ROOT, '..', 'spec');
export const DATA_ROOT = process.env.TH_DATA_ROOT ?? path.join(STUDIO_ROOT, 'data');
/** A bank's configuration, one folder per bank (plan W6, D5 as amended 2026-11-21). */
export const BANKS_ROOT = process.env.TH_BANKS_ROOT ?? path.resolve(STUDIO_ROOT, '..', 'content-configurations', 'banks');

export interface StudioEnv extends RunnerEnv {
  schemas: SchemaValidator;
  specRoot: string;
  studioRoot: string;
}

/** The bank folders under a banks root that hold a bank pack (a `pack.yaml`). */
export function bankPackDirs(root = BANKS_ROOT): string[] {
  if (!fs.existsSync(root)) return [];
  return fs.readdirSync(root, { withFileTypes: true })
    .filter((d) => d.isDirectory() && fs.existsSync(path.join(root, d.name, 'pack.yaml')))
    .map((d) => path.join(root, d.name))
    .sort();
}

/** A bank pack folder, from a path or from a folder name under the banks root. */
export function resolveBankPack(ref: string): string {
  if (fs.existsSync(path.join(ref, 'pack.yaml'))) return path.resolve(ref);
  const named = path.join(BANKS_ROOT, ref);
  if (fs.existsSync(path.join(named, 'pack.yaml'))) return named;
  throw new Error(`No bank pack at ${ref}, nor at ${named}`);
}

/**
 * A studio environment over the spec. `banks` adds bank packs from their own folders (names under the
 * banks root, or paths); without it, `TH_BANKS` (comma separated) names them. None is loaded by default.
 */
export function openEnv(opts: { dataRoot?: string; banks?: string[] } = {}): StudioEnv {
  const dataRoot = opts.dataRoot ?? DATA_ROOT;
  const banks = (opts.banks ?? (process.env.TH_BANKS ?? '').split(',').map((s) => s.trim()).filter(Boolean)).map(resolveBankPack);
  const store = Store.open(SPEC_ROOT, dataRoot, [], banks);
  // Scripts and samples a designer writes live in the workspace, after the factory's own.
  const ws = (sub: string) => (dataRoot ? [path.join(path.resolve(dataRoot), sub)] : []);
  return {
    store,
    scripts: new ScriptRegistry([path.join(STUDIO_ROOT, 'scripts'), ...ws('scripts')]),
    // A bank's tests read their synthetic samples from the bank's own folder.
    samplesDirs: [path.join(STUDIO_ROOT, 'fixtures', 'samples'), ...banks.map((b) => path.join(b, 'tests', 'samples')), ...ws('samples')],
    schemas: new SchemaValidator(path.join(SPEC_ROOT, 'schemas')),
    specRoot: SPEC_ROOT,
    studioRoot: STUDIO_ROOT,
  };
}
