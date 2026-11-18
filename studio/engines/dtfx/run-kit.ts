import { openEnv } from '../../src/env.js';
import { runKit } from '../../src/conformance/kit.js';
import { spawnSync } from 'node:child_process';
import { blockedByWindows } from '../../src/deploy/blocked.js';
import { buildHarness, dtfxAdapter, HARNESS_DLL } from './adapter.js';

// Run the conformance kit on DTFx: `npx tsx engines/dtfx/run-kit.ts [CK-ID...]` from studio/.
// The comparison is the kit runner's own (compareObservation); only the adapter differs.

const args = process.argv.slice(2).filter((a) => !a.startsWith('--'));
const verbose = process.argv.includes('--verbose');
const env = openEnv({ dataRoot: '' });
buildHarness();
// One start of the harness with no input: if Windows refuses to load it, stop here with the reason.
const probe = spawnSync('dotnet', [HARNESS_DLL], { encoding: 'utf8', timeout: 60000 });
const blocked = blockedByWindows(`${probe.stdout ?? ''}${probe.stderr ?? ''}`);
if (blocked) {
  console.error(blocked);
  process.exit(1);
}
const adapter = dtfxAdapter(env);
const t0 = Date.now();
const res = await runKit(env, args, adapter);
let pass = 0;
for (const r of res) {
  if (r.passed) pass++;
  const failed = r.checks.filter((c) => !c.passed);
  const why = r.error
    ? `error ${r.error}`
    : failed.map((c: any) => (c.what === 'trace' ? `trace ${c.at}` : `${c.what}: expected ${JSON.stringify(c.expected)}, got ${JSON.stringify(c.actual)}`)).join('; ');
  console.log(`${r.passed ? 'PASS' : 'FAIL'}  ${r.id}${r.passed ? '' : '  ' + why}${r.notes?.length ? '  (' + r.notes.join('; ') + ')' : ''}`);
  if (verbose || !r.passed) {
    console.log(`      expected ${JSON.stringify(r.expected)}`);
    console.log(`      actual   ${JSON.stringify(r.actual)}`);
    const e = adapter.engine[r.id];
    if (e) console.log(`      engine   ${e.orchestrationInstances} orchestration instance(s), ${e.executions} execution(s), ${e.episodes} episode(s) replayed, ${e.activityRuns} activity run(s), ${e.historyEvents} history events, ${e.lateResultsAbandoned} late result(s) abandoned, virtual end ${e.virtualEnd}${e.log?.length ? ', ' + e.log.join('; ') : ''}`);
  }
}
console.log(`${pass} of ${res.length} conformance scenarios pass on DTFx (${((Date.now() - t0) / 1000).toFixed(0)} s wall time)`);
process.exitCode = pass === res.length ? 0 : 1;
