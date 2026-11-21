import { openReadEnv } from '../../src/env.js';
import { runKit } from '../../src/conformance/kit.js';
import { spawnSync } from 'node:child_process';
import { blockedByWindows } from '../../src/deploy/blocked.js';
import { buildElsaHarness, elsaAdapter, ELSA_HARNESS_DLL } from './adapter.js';

// Run the conformance kit on Elsa Workflows: `npx tsx engines/elsa/run-kit.ts [CK-ID...]` from studio/.
// The comparison is the kit runner's own (compareObservation); only the adapter differs.

const args = process.argv.slice(2).filter((a) => !a.startsWith('--'));
const verbose = process.argv.includes('--verbose');
// The content: the connected branch, or an in-memory fake seeded from content-configurations/ (R-11-21).
const { env } = await openReadEnv();
buildElsaHarness();
// One start of the harness with no input: if Windows refuses to load it, stop here with the reason.
const probe = spawnSync('dotnet', [ELSA_HARNESS_DLL], { encoding: 'utf8', timeout: 60000 });
const blocked = blockedByWindows(`${probe.stdout ?? ''}${probe.stderr ?? ''}`);
if (blocked) {
  console.error(blocked);
  process.exit(1);
}
const adapter = elsaAdapter(env);
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
    if (e) console.log(`      engine   ${e.orchestrationInstances} engine instance(s), ${e.executions} Elsa workflow execution(s), ${e.episodes} bookmark resume(s), ${e.activityRuns} activity run(s), ${e.historyEvents} KB of stored workflow state, ${e.lateResultsAbandoned} late result(s) abandoned, virtual end ${e.virtualEnd}${e.log?.length ? ', ' + e.log.join('; ') : ''}`);
  }
}
console.log(`${pass} of ${res.length} conformance scenarios pass on Elsa (${((Date.now() - t0) / 1000).toFixed(0)} s wall time)`);
process.exitCode = pass === res.length ? 0 : 1;
