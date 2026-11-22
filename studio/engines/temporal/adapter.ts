import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { BuiltScenario, KitAdapter, KitObservation } from '../../src/conformance/kit.js';
import type { StudioEnv } from '../../src/env.js';
import { exportedDocuments } from '../dtfx/adapter.js';

// The Temporal adapter for the kit runner. It hands the scenario to the runtime's Temporal harness
// (runtime/tools/TransactHub.TemporalHarness), which runs it on Temporal and reports what it observed. The engine gets the
// expanded form only (R-09-17), and the comparison is the kit runner's own.

const here = path.dirname(fileURLToPath(import.meta.url));
export const TEMPORAL_HARNESS_DIR = path.resolve(here, '..', '..', '..', 'runtime', 'tools', 'TransactHub.TemporalHarness');
export const TEMPORAL_HARNESS_DLL = path.join(TEMPORAL_HARNESS_DIR, 'bin', 'Release', 'net10.0', 'TransactHub.TemporalHarness.dll');

/** Build the harness (dotnet build, Release); returns the path of the assembly. */
export function buildTemporalHarness(): string {
  const r = spawnSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: TEMPORAL_HARNESS_DIR, encoding: 'utf8', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
  if (r.status !== 0) throw new Error(`dotnet build failed:\n${r.stdout}\n${r.stderr}`);
  return TEMPORAL_HARNESS_DLL;
}

/** The Temporal engine binding (spec/examples/bindings): task naming and the chunk size come from it. */
export function temporalBinding(env: StudioEnv): any {
  const b = env.store.byKind('engine-binding').map((e) => e.doc).find((d: any) => d.target === 'temporal');
  if (!b) throw new Error('No engine binding for temporal');
  if (b.taskNaming !== '<name>@<release>') throw new Error(`The harness registers tasks as <name>@<release>; the binding says ${b.taskNaming}`);
  return b;
}

/**
 * Scenarios the time-skipping test server cannot run, so the adapter runs them on Temporal's real dev server
 * (its CLI image, in Docker), in real time. CK-FOREACH-02 (10,000 items in chunks that continue as new)
 * makes the Java test server refuse a workflow task ("invalid history builder state for action") about
 * 1,100 items in; the real server runs it in about two minutes. It waits for nothing, so real time is fine.
 */
// CK-TIMEOUT-03 (R-05-54) must run on a real server: only there does processing take time on the server's clock, so only
// there does it show that an attempt's timeout measures its own execution and not the item loop's.
export const REAL_SERVER_SCENARIOS = new Set(['CK-FOREACH-02', 'CK-TIMEOUT-03']);

let devServer: string | undefined;
/** Start Temporal's dev server once per process (TH_TEMPORAL_ADDRESS, if set, is used instead). */
function realServer(): string {
  if (process.env.TH_TEMPORAL_ADDRESS) return process.env.TH_TEMPORAL_ADDRESS;
  if (devServer) return devServer;
  const name = `th-kit-temporal-${process.pid}`;
  const r = spawnSync('docker', ['run', '-d', '--rm', '--name', name, '-p', '127.0.0.1::7233', 'temporalio/temporal:1.5.1', 'server', 'start-dev', '--ip', '0.0.0.0', '--headless'], { encoding: 'utf8' });
  if (r.status !== 0) throw new Error(`Temporal's dev server did not start (Docker needed for ${[...REAL_SERVER_SCENARIOS].join(', ')}): ${r.stderr}`);
  process.on('exit', () => spawnSync('docker', ['rm', '-f', name]));
  const port = spawnSync('docker', ['port', name, '7233'], { encoding: 'utf8' }).stdout.trim().split(':').pop();
  for (let i = 0; i < 60; i++) {
    if (spawnSync('docker', ['exec', name, 'temporal', 'operator', 'cluster', 'health'], { encoding: 'utf8' }).stdout.includes('SERVING')) break;
    spawnSync('sleep', ['1']);
  }
  devServer = `127.0.0.1:${port}`;
  return devServer;
}

export function temporalAdapter(env: StudioEnv, opts: { chunkSize?: number; keepInputs?: boolean } = {}): KitAdapter & { engine: Record<string, any> } {
  const engine: Record<string, any> = {};
  const binding = temporalBinding(env);
  const chunkSize = opts.chunkSize ?? (process.env.TH_KIT_CHUNK ? Number(process.env.TH_KIT_CHUNK) : undefined) ?? binding.defaults?.forEachChunk ?? 500;
  return {
    target: 'temporal',
    engine,
    async run(b: BuiltScenario): Promise<KitObservation> {
      if (!fs.existsSync(TEMPORAL_HARNESS_DLL)) buildTemporalHarness();
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'temporal-kit-'));
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
        const env = REAL_SERVER_SCENARIOS.has(b.id) ? { ...process.env, TH_TEMPORAL_ADDRESS: realServer() } : process.env;
        const r = spawnSync('dotnet', [TEMPORAL_HARNESS_DLL, file], { encoding: 'utf8', maxBuffer: 512 * 1024 * 1024, timeout: Number(process.env.TH_KIT_TIMEOUT_MIN ?? 15) * 60 * 1000, env });
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
        if (!opts.keepInputs && !process.env.TH_KIT_KEEP) fs.rmSync(dir, { recursive: true, force: true });
      }
    },
  };
}
