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

export interface StudioEnv extends RunnerEnv {
  schemas: SchemaValidator;
  specRoot: string;
  studioRoot: string;
}

export function openEnv(opts: { dataRoot?: string } = {}): StudioEnv {
  const dataRoot = opts.dataRoot ?? DATA_ROOT;
  const store = Store.open(SPEC_ROOT, dataRoot);
  // Scripts and samples a designer writes live in the workspace, after the factory's own.
  const ws = (sub: string) => (dataRoot ? [path.join(path.resolve(dataRoot), sub)] : []);
  return {
    store,
    scripts: new ScriptRegistry([path.join(STUDIO_ROOT, 'scripts'), ...ws('scripts')]),
    samplesDirs: [path.join(STUDIO_ROOT, 'fixtures', 'samples'), ...ws('samples')],
    schemas: new SchemaValidator(path.join(SPEC_ROOT, 'schemas')),
    specRoot: SPEC_ROOT,
    studioRoot: STUDIO_ROOT,
  };
}
