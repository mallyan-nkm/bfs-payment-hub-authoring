import { openEnv } from './env.js';
import { runAllTests, runTest } from './testing/runner.js';

// Command line: `npm run studio -- test [ID...]`, `kit`, `check`, `export <workflow> <target>`.

const USAGE = 'Usage: studio test [ID...] | kit [CK-ID...] | check | export <workflow> <target> | export-zip <workflow> <target> <out.zip> [--with-simulators] [--tenant t] | deploy <package> --workflow <name> [--inbound file] [--date YYYY-MM-DD] [--tenant t] [--bench port=simulator:scenario ...]';

/** A mistake in the command line: the usage and exit code 2. */
class UsageError extends Error {}

async function main() {
  const [cmd, ...args] = process.argv.slice(2);
  const need = (ok: unknown, what: string) => {
    if (!ok) throw new UsageError(what);
  };
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
    need(args[0], 'export needs a workflow');
    const { exportPackage } = await import('./export/exporter.js');
    const pkg = exportPackage(env, { workflows: [args[0]], target: (args[1] ?? 'temporal') as any });
    console.log(JSON.stringify(pkg.manifest, null, 2));
    return;
  }
  if (cmd === 'deploy') {
    // studio deploy <package.zip|dir> --workflow <name> [--inbound <file>] [--date YYYY-MM-DD] [--tenant t] [--bench port=simulator:scenario ...]
    const { deploy } = await import('./deploy/deploy.js');
    const opt = (k: string) => { const i = args.indexOf(`--${k}`); return i >= 0 ? args[i + 1] : undefined; };
    need(args[0] && !args[0].startsWith('--'), 'deploy needs a package (a .zip or a directory)');
    need(opt('workflow'), 'deploy needs --workflow <name>');
    const bench = args.flatMap((a, i) => (args[i - 1] === '--bench' ? [a] : [])).map((b) => {
      const m = /^([^=]+)=([^:]+):(.+)$/.exec(b);
      need(m, `--bench ${b}: write it as port=simulator:scenario`);
      return { port: m![1], simulator: m![2], scenario: m![3] };
    });
    const tenant = opt('tenant');
    const r = await deploy({
      packagePath: args[0],
      workflow: opt('workflow')!,
      studioRoot: env.studioRoot,
      environment: { evaluationDate: opt('date') ?? '2026-11-17', tenant, bench, configurationSet: tenant ? env.store.configurationSet(tenant) : undefined },
      inbound: opt('inbound') ? { payload: (await import('node:fs')).readFileSync(opt('inbound')!, 'utf8') } : undefined,
    });
    for (const l of r.log) console.log(l);
    for (const w of r.workers) console.log(`worker ${w.queue} (pid ${w.pid}) served ${JSON.stringify(w.served)}, refused ${w.refused}`);
    console.log(`outcome ${r.outcome}${r.failed ? ' (' + r.failed + ')' : ''}`);
    console.log(`trace  ${r.trace.join(' | ')}`);
    console.log(`detail ${r.detail.join(' | ')}`);
    process.exitCode = r.failed ? 1 : 0;
    return;
  }
  if (cmd === 'export-zip') {
    // studio export-zip <workflow> <target> <out.zip> [--with-simulators] [--tenant t]
    need(args[0] && args[1] && args[2], 'export-zip needs a workflow, a target and the zip to write');
    const { exportPackage, exportZip } = await import('./export/exporter.js');
    const pkg = exportPackage(env, { workflows: [args[0]], target: args[1] as any, withSimulators: args.includes('--with-simulators'), tenant: args.includes('--tenant') ? args[args.indexOf('--tenant') + 1] : undefined });
    (await import('node:fs')).writeFileSync(args[2], exportZip(pkg));
    console.log(`${args[2]}: ${Object.keys(pkg.files).length} files, workers ${pkg.manifest.workers.map((w: any) => w.queue).join(', ')}`);
    return;
  }
  if (cmd && cmd !== 'help' && cmd !== '--help') {
    console.error(`Unknown command ${cmd}`);
    process.exitCode = 2;
  }
  console.log(USAGE);
}

main().catch((e) => {
  if (e instanceof UsageError) {
    console.error(e.message);
    console.error(USAGE);
    process.exitCode = 2;
    return;
  }
  console.error(e?.message ?? e);
  process.exitCode = 1;
});
