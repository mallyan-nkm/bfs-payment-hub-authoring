import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { compareReleases } from '../../src/core/names.js';
import { callsComposite, expandWorkflow } from '../../src/core/expand.js';
import type { BuiltScenario, KitAdapter, KitObservation } from '../../src/conformance/kit.js';
import type { StudioEnv } from '../../src/env.js';

// The DTFx adapter for the kit runner (C21-S1, C23-S2). It hands the scenario to the .NET harness
// in ./harness, which runs it on the Durable Task Framework and reports what it observed. The
// engine gets the expanded form only (R-09-17): the expansion happens here, at export.

const here = path.dirname(fileURLToPath(import.meta.url));
export const HARNESS_DIR = path.join(here, 'harness');
export const HARNESS_DLL = path.join(HARNESS_DIR, 'bin', 'Release', 'net8.0', 'DtfxHarness.dll');

/** Build the harness once (dotnet build); returns the path of the assembly. */
export function buildHarness(): string {
  const r = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: HARNESS_DIR, encoding: 'utf8', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
  if (r.status !== 0) throw new Error(`dotnet build failed:\n${r.stdout}\n${r.stderr}`);
  return HARNESS_DLL;
}

/** The documents as exported for an engine: every workflow and hook in its expanded form. */
export function exportedDocuments(docs: Record<string, any>): Record<string, any> {
  const resolve = (name: string) => {
    if (docs[name]) return docs[name];
    const keys = Object.keys(docs).filter((k) => k.startsWith(name + '@')).sort((a, b) => compareReleases(a.split('@')[1], b.split('@')[1]));
    return keys.length ? docs[keys[keys.length - 1]] : undefined;
  };
  const out: Record<string, any> = {};
  for (const [k, d] of Object.entries(docs)) {
    if (d?.kind === 'workflow' && callsComposite(d, resolve)) out[k] = expandWorkflow(d, resolve);
    else if (d?.kind === 'hook' && callsComposite({ ...d, kind: 'workflow' }, resolve)) out[k] = { ...expandWorkflow({ ...d, kind: 'workflow' }, resolve), kind: 'hook' };
    else out[k] = d;
  }
  return out;
}

export interface DtfxOptions {
  chunkSize?: number; // overrides the binding's chunk size for large batches (R-09-13)
  keepInputs?: boolean;
}

/** The DTFx engine binding (spec/examples/bindings): task naming and the chunk size come from it. */
export function dtfxBinding(env: StudioEnv): any {
  const b = env.store.byKind('engine-binding').map((e) => e.doc).find((d: any) => d.target === 'dtfx');
  if (!b) throw new Error('No engine binding for dtfx');
  if (b.taskNaming !== '<name>@<release>') throw new Error(`The harness registers tasks as <name>@<release>; the binding says ${b.taskNaming}`);
  return b;
}

export function dtfxAdapter(env: StudioEnv, opts: DtfxOptions = {}): KitAdapter & { engine: Record<string, any> } {
  const engine: Record<string, any> = {};
  const binding = dtfxBinding(env);
  const chunkSize = opts.chunkSize ?? binding.defaults?.forEachChunk ?? 500;
  return {
    target: 'dtfx',
    engine,
    async run(b: BuiltScenario): Promise<KitObservation> {
      if (!fs.existsSync(HARNESS_DLL)) buildHarness();
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dtfx-kit-'));
      const file = path.join(dir, `${b.id}.json`);
      const input = {
        studioRoot: env.studioRoot,
        chunkSize,
        itemConcurrency: binding.defaults?.forEachConcurrency ?? 1,
        built: { id: b.id, spec: b.spec, workflow: b.workflow, config: b.config, context: b.context },
        documents: exportedDocuments(b.documents),
      };
      fs.writeFileSync(file, JSON.stringify(input));
      try {
        const r = spawnSync('dotnet', [HARNESS_DLL, file], { encoding: 'utf8', maxBuffer: 512 * 1024 * 1024, timeout: 15 * 60 * 1000 });
        const lines = (r.stdout ?? '').split('\n');
        const obsLine = lines.find((l) => l.startsWith('OBSERVATION '));
        if (!obsLine) {
          const err = lines.find((l) => l.startsWith('ERROR '));
          throw new Error(err ? JSON.parse(err.slice(6)) : `harness exited ${r.status} ${r.signal ?? ''}: ${(r.stderr ?? '').slice(-2000)}`);
        }
        const obs = JSON.parse(obsLine.slice('OBSERVATION '.length));
        engine[b.id] = obs.engine;
        return obs as KitObservation;
      } finally {
        if (!opts.keepInputs) fs.rmSync(dir, { recursive: true, force: true });
      }
    },
  };
}
