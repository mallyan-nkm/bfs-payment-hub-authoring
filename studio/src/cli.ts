import { openEnv } from './env.js';
import { runAllTests, runTest } from './testing/runner.js';

// Command line: `npm run studio -- test [ID...]`, `kit`, `check`, `export <workflow> <target>`.

async function main() {
  const [cmd, ...args] = process.argv.slice(2);
  const env = openEnv({ dataRoot: process.env.TH_DATA_ROOT ?? '' });
  if (cmd === 'test') {
    const reports = args.length
      ? await Promise.all(args.map(async (id) => { const t = env.store.test(id); if (!t) throw new Error(`No test ${id}`); return runTest(env, t.doc); }))
      : await runAllTests(env);
    let pass = 0;
    for (const r of reports) {
      if (r.passed) pass++;
      console.log(`${r.passed ? 'PASS' : 'FAIL'}  ${r.id}  ${r.target}  outcome=${r.outcome}  elapsed=${r.elapsed}`);
      if (!r.passed || args.length) {
        for (const c of r.checks.filter((c) => !c.passed || args.length)) console.log(`      ${c.passed ? 'ok ' : 'BAD'} ${c.what}: expected ${JSON.stringify(c.expected)} got ${JSON.stringify(c.actual)}${c.note ? ' (' + c.note + ')' : ''}`);
        if (r.error) console.log('      error: ' + r.error.split('\n').slice(0, 4).join('\n      '));
        if (args.length) console.log('      trace: ' + r.trace.join(' | '));
        for (const w of r.warnings) console.log('      note: ' + w);
      }
    }
    console.log(`${pass} of ${reports.length} tests pass`);
    process.exitCode = pass === reports.length ? 0 : 1;
    return;
  }
  if (cmd === 'kit') {
    const { runKit } = await import('./conformance/kit.js');
    const res = await runKit(env, args);
    let pass = 0;
    for (const r of res) {
      if (r.passed) pass++;
      console.log(`${r.passed ? 'PASS' : 'FAIL'}  ${r.id}  ${r.passed ? '' : 'expected ' + JSON.stringify(r.expected) + ' got ' + JSON.stringify(r.actual)}${r.error ? ' error ' + r.error : ''}${r.notes?.length ? ' ' + r.notes.join('; ') : ''}`);
    }
    console.log(`${pass} of ${res.length} conformance scenarios pass on the studio interpreter`);
    process.exitCode = pass === res.length ? 0 : 1;
    return;
  }
  if (cmd === 'check') {
    const { checkAll } = await import('./checks/checks.js');
    const issues = checkAll(env);
    for (const i of issues) console.log(`${i.severity.toUpperCase().padEnd(5)} ${i.code}  ${i.key}${i.step ? ' step ' + i.step : ''}: ${i.message}`);
    const errors = issues.filter((i) => i.severity === 'error').length;
    console.log(`${errors} error(s), ${issues.length - errors} warning(s)`);
    process.exitCode = errors ? 1 : 0;
    return;
  }
  if (cmd === 'export') {
    const { exportPackage } = await import('./export/exporter.js');
    const pkg = exportPackage(env, { workflows: [args[0]], target: (args[1] ?? 'temporal') as any });
    console.log(JSON.stringify(pkg.manifest, null, 2));
    return;
  }
  console.log('Usage: studio test [ID...] | kit [CK-ID...] | check | export <workflow> <temporal|dtfx|flowable>');
}

main().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});
