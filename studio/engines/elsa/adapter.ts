import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { BuiltScenario, KitAdapter, KitObservation } from '../../src/conformance/kit.js';
import type { StudioEnv } from '../../src/env.js';
import { exportedDocuments } from '../dtfx/adapter.js';

// The Elsa adapter for the kit runner (I-30). It hands the scenario to the .NET harness in ./harness,
// which runs it on Elsa Workflows 3 and reports what it observed. As for DTFx, the engine gets the
// expanded form only (R-09-17), and the comparison is the kit runner's own.

const here = path.dirname(fileURLToPath(import.meta.url));
export const ELSA_HARNESS_DIR = path.join(here, 'harness');
export const ELSA_HARNESS_DLL = path.join(ELSA_HARNESS_DIR, 'bin', 'Release', 'net8.0', 'ElsaHarness.dll');

/** Build the harness (dotnet build); returns the path of the assembly. */
export function buildElsaHarness(): string {
  const r = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: ELSA_HARNESS_DIR, encoding: 'utf8', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
  if (r.status !== 0) throw new Error(`dotnet build failed:\n${r.stdout}\n${r.stderr}`);
  return ELSA_HARNESS_DLL;
}

/** The Elsa engine binding (spec/examples/bindings): task naming and the chunk size come from it. */
export function elsaBinding(env: StudioEnv): any {
  const b = env.store.byKind('engine-binding').map((e) => e.doc).find((d: any) => d.target === 'elsa');
  if (!b) throw new Error('No engine binding for elsa');
  if (b.taskNaming !== '<name>@<release>') throw new Error(`The harness registers tasks as <name>@<release>; the binding says ${b.taskNaming}`);
  return b;
}

export function elsaAdapter(env: StudioEnv, opts: { chunkSize?: number; keepInputs?: boolean } = {}): KitAdapter & { engine: Record<string, any> } {
  const engine: Record<string, any> = {};
  const binding = elsaBinding(env);
  const chunkSize = opts.chunkSize ?? binding.defaults?.forEachChunk ?? 500;
  return {
    target: 'elsa',
    engine,
    async run(b: BuiltScenario): Promise<KitObservation> {
      if (!fs.existsSync(ELSA_HARNESS_DLL)) buildElsaHarness();
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'elsa-kit-'));
      const file = path.join(dir, `${b.id}.json`);
      const input = {
        studioRoot: env.studioRoot,
        chunkSize,
        built: { id: b.id, spec: b.spec, workflow: b.workflow, config: b.config, context: b.context },
        documents: exportedDocuments(b.documents),
      };
      fs.writeFileSync(file, JSON.stringify(input));
      try {
        const r = spawnSync('dotnet', [ELSA_HARNESS_DLL, file], { encoding: 'utf8', maxBuffer: 512 * 1024 * 1024, timeout: 15 * 60 * 1000 });
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
