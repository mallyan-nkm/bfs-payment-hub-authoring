import { CONTENT_DIR, openReadEnv, type StudioEnv } from './env.js';
import { runAllTests, runTest } from './testing/runner.js';

// Command line: `npm run studio -- test [ID...]`, `kit`, `check`, `export <workflow> <target>`.
// `--bank <name>` (repeatable, any command) adds a bank pack from its folder on the branch (Issue I-43).
//
// Content (R-11-21): the connected branch when TH_CONTENT_REPO is set (with TH_CONTENT_BRANCH,
// TH_GITHUB_TOKEN, TH_GITHUB_API); otherwise an in-memory fake GitHub seeded from this repository's
// content-configurations/. Either way nothing is written to disk, and the command says which it read.

const USAGE = 'Usage: studio [--bank <name> ...] test [ID...] | kit [CK-ID...] | check | seed-content [--actor a] [--write-local] | bank-pack <name> [--write] | export <workflow> <target> | export-zip <workflow> <target> <out.zip> [--with-simulators] [--tenant t] | import <bundle.zip|dir> [--tenant t] [--bank-tenant b] [--actor a] [--bank-actor x] [--apply] [--json] | import-sample <out.zip> | deploy <package> --workflow <name> [--inbound file [--message <start message>]] [--date YYYY-MM-DD] [--tenant t] [--config configuration-set.yaml] [--bench port=simulator:scenario | port=runtime ...]';

/** A mistake in the command line: the usage and exit code 2. */
class UsageError extends Error {}

const COMMANDS = ['test', 'kit', 'check', 'seed-content', 'bank-pack', 'export', 'export-zip', 'import', 'import-sample', 'deploy'];

async function main() {
  const argv = process.argv.slice(2);
  const banks = argv.flatMap((a, i) => (argv[i - 1] === '--bank' ? [a] : []));
  const [cmd, ...args] = argv.filter((a, i) => a !== '--bank' && argv[i - 1] !== '--bank');
  if (cmd === 'help' || cmd === '--help' || !cmd) {
    console.log(USAGE);
    return;
  }
  if (!COMMANDS.includes(cmd)) {
    console.error(`Unknown command ${cmd}`);
    console.log(USAGE);
    process.exitCode = 2;
    return;
  }
  if (cmd === 'seed-content' && args.includes('--write-local')) {
    // Regenerate this repository's content-configurations/ as the seed publishes it: the seed runs as a
    // change set against an in-memory fake seeded from the folder, and the fake's tree is copied back.
    const { writeLocalSeed } = await import('./content/seed-local.js');
    const r = await writeLocalSeed(opt(args, 'actor'));
    console.log(r.status === 'unchanged' ? `content-configurations/ is current: ${r.unchanged} seeded files, nothing to write` : `content-configurations/: ${r.written.length} file(s) written by change set ${r.changeSet}${r.replaced.length ? `; ${r.replaced.length} regenerated from a changed source` : ''}`);
    return;
  }
  const { env, where, stop } = await openReadEnv(banks.length ? { banks } : {});
  console.error(`content: ${where}`);
  try {
    await run(cmd, args, env, where);
    await env.store.flush();
  } finally {
    await stop();
  }
}

const opt = (args: string[], k: string) => {
  const i = args.indexOf(`--${k}`);
  return i >= 0 ? args[i + 1] : undefined;
};

async function run(cmd: string, args: string[], env: StudioEnv, where: string) {
  const need = (ok: unknown, what: string) => {
    if (!ok) throw new UsageError(what);
  };
  const local = where.startsWith('an in-memory');
  if (cmd === 'seed-content') {
    const { seedContent, seedPlan } = await import('./content/seed.js');
    if (local) {
      // Not connected: say whether this repository's content-configurations/ holds the seed as it is now.
      const plan = seedPlan(env);
      for (const f of plan.clash) console.log(`differs  ${f.path} (from ${f.source})`);
      for (const f of plan.add) console.log(`missing  ${f.path} (from ${f.source})`);
      console.log(`${plan.same} of ${plan.files.length} seeded files are current in ${where}${plan.add.length + plan.clash.length ? '; run `npm run studio -- seed-content --write-local`' : ''}`);
      process.exitCode = plan.add.length + plan.clash.length ? 1 : 0;
      return;
    }
    const r = await seedContent(env, opt(args, 'actor'));
    console.log(r.status === 'unchanged' ? `unchanged: all ${r.unchanged} seeded files are on ${r.branch}` : `committed ${r.commit} on ${r.branch}: change set ${r.changeSet}, ${r.added.length} file(s) added, ${r.unchanged} already there`);
    return;
  }
  if (cmd === 'bank-pack') {
    // studio bank-pack <name> [--write]: the lock file of a bank pack, as tools/build_factory.py writes the example bank's.
    need(args[0] && !args[0].startsWith('--'), 'bank-pack needs a bank: a folder name under content-configurations/banks');
    const fs = await import('node:fs');
    const path = await import('node:path');
    const { packManifest } = await import('./governance/packs.js');
    const { toYaml } = await import('./core/yaml.js');
    const { SEED_HEADER } = await import('./core/store.js');
    const tenant = [...env.store.bankPacks].find(([, folder]) => folder === args[0])?.[0];
    if (!tenant) throw new Error(`No bank pack ${args[0]} is loaded: pass --bank ${args[0]}`);
    const text = '# Generated by `npm run studio -- bank-pack`. The lock file of this pack.\n' + toYaml(packManifest(env, 'b', tenant));
    if (args.includes('--write')) {
      // A repository authoring step, like tools/build_factory.py: it writes this repository's folder, not the studio's store.
      const file = path.join(CONTENT_DIR, 'banks', args[0], 'pack.yaml');
      if (SEED_HEADER.test(fs.readFileSync(file, 'utf8'))) throw new Error(`${file} is seeded from spec/: run tools/build_factory.py and seed again`);
      fs.writeFileSync(file, text);
      console.log(`${file}: written`);
    } else process.stdout.write(text);
    return;
  }
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
    // studio deploy <package.zip|dir> --workflow <name> [--inbound <file>] [--date YYYY-MM-DD] [--tenant t] [--config configuration-set.yaml] [--bench port=simulator:scenario ...]
    const { deploy } = await import('./deploy/deploy.js');
    const opt = (k: string) => { const i = args.indexOf(`--${k}`); return i >= 0 ? args[i + 1] : undefined; };
    need(args[0] && !args[0].startsWith('--'), 'deploy needs a package (a .zip or a directory)');
    need(opt('workflow'), 'deploy needs --workflow <name>');
    const bench = args.flatMap((a, i) => (args[i - 1] === '--bench' ? [a] : [])).map((b) => {
      // port=runtime: the runtime's native host answers the port, through the bank's port binding (R-08-15).
      const r = /^([^=]+)=runtime$/.exec(b);
      if (r) return { port: r[1], simulator: 'runtime', scenario: '' };
      const m = /^([^=]+)=([^:]+):(.+)$/.exec(b);
      need(m, `--bench ${b}: write it as port=simulator:scenario or port=runtime`);
      return { port: m![1], simulator: m![2], scenario: m![3] };
    });
    const tenant = opt('tenant');
    const r = await deploy({
      packagePath: args[0],
      workflow: opt('workflow')!,
      studioRoot: env.studioRoot,
      environment: { evaluationDate: opt('date') ?? '2026-11-17', tenant, bench, configurationSet: opt('config') ? (await import('./core/yaml.js')).parseYaml((await import('node:fs')).readFileSync(opt('config')!, 'utf8')) : undefined },
      inbound: opt('inbound') ? { payload: (await import('node:fs')).readFileSync(opt('inbound')!, 'utf8'), ...(opt('message') ? { message: opt('message') } : {}) } : undefined,
    });
    for (const l of r.log) console.log(l);
    for (const w of r.workers) console.log(`worker ${w.queue} (pid ${w.pid}) served ${JSON.stringify(w.served)}, refused ${w.refused}`);
    console.log(`outcome ${r.outcome}${r.failed ? ' (' + r.failed + ')' : ''}`);
    console.log(`trace  ${r.trace.join(' | ')}`);
    console.log(`detail ${r.detail.join(' | ')}`);
    process.exitCode = r.failed ? 1 : 0;
    return;
  }
  if (cmd === 'import' || cmd === 'import-sample') {
    // studio import <bundle.zip|dir> [--tenant t] [--bank-tenant b] [--actor a] [--bank-actor x] [--apply] [--json]
    const fs = await import('node:fs');
    const { readBundle, readBundleDir, analyse, apply } = await import('./import/bundle.js');
    const { bundleZip, wpsTrainingBundle } = await import('./import/sample.js');
    const opt = (k: string) => { const i = args.indexOf(`--${k}`); return i >= 0 ? args[i + 1] : undefined; };
    if (cmd === 'import-sample') {
      need(args[0], 'import-sample needs the zip to write');
      fs.writeFileSync(args[0], bundleZip(wpsTrainingBundle(env)));
      console.log(`${args[0]}: the training extraction bundle (WPS as uae.wpsx)`);
      return;
    }
    need(args[0] && !args[0].startsWith('--'), 'import needs a bundle: a .zip or a directory');
    const bundle = fs.statSync(args[0]).isDirectory() ? readBundleDir(args[0]) : readBundle(fs.readFileSync(args[0]));
    // Applying saves drafts on their change set branches: on the connected branch's repository, or in memory only.
    const ienv = env;
    const base = { tenant: opt('tenant') ?? 'owner', bankTenant: opt('bank-tenant') };
    const out: any = args.includes('--apply')
      ? await apply(ienv, bundle, { ...base, actor: opt('actor') ?? 'shared.designer', bankActor: opt('bank-actor'), force: args.includes('--force') })
      : { report: await analyse(ienv, bundle, base) };
    const r = out.report;
    if (args.includes('--json')) { console.log(JSON.stringify(out, null, 2)); process.exitCode = r.ok ? 0 : 1; return; }
    console.log(`bundle   ${r.manifest.scope} from ${r.manifest.source?.system ?? '?'} ${r.manifest.source?.repository ?? ''}@${r.manifest.source?.revision ?? ''}`);
    console.log(`content  ${Object.entries(r.summary).filter(([, v]) => v).map(([k, v]) => `${v} ${k}`).join(', ')}`);
    const sf = r.inventory.sourceFiles;
    console.log(`source   ${sf.total} files: ${sf.inventoried} inventoried, ${sf.excluded} excluded, ${sf.missing.length} missing; ${r.inventory.total} items: ${Object.entries(r.inventory.byTarget).map(([k, v]) => `${v} ${k}`).join(', ')}`);
    for (const t of r.tests) console.log(`test     ${t.passed ? 'PASS' : 'FAIL'}  ${t.id}  ${t.outcome ?? ''}${t.passed ? '' : '  ' + (t.error ?? t.failed?.join('; ') ?? '')}`);
    const all = [...r.issues, ...r.items.flatMap((i: any) => i.issues), ...r.scripts.flatMap((s: any) => s.issues), ...r.samples.flatMap((s: any) => s.issues)];
    for (const i of all.filter((x: any) => x.severity === 'error')) console.log(`error    ${i.code} ${i.where}: ${i.message}`);
    for (const i of all.filter((x: any) => x.severity === 'warning')) console.log(`warning  ${i.code} ${i.where}: ${i.message}`);
    for (const u of r.inventory.unmapped) console.log(`unmapped ${u.id} ${u.source?.path}${u.source?.lines ? ':' + u.source.lines : ''}: ${u.note}`);
    if (out.applied) {
      console.log(`saved    ${out.saved.length} as drafts; ${out.skipped.length} unchanged and reused`);
      if (local) console.log(`note     Saved to ${where}: gone when this command ends. Set TH_CONTENT_REPO to save the drafts on the content branch.`);
      for (const cs of out.changeSets) console.log(`set      ${cs.join(', ')}`);
      console.log('Nothing is published: a checker approves each change set in Governance.');
    } else if (args.includes('--apply')) console.log('Not imported: the bundle has errors. Fix them, or pass --force to import with them.');
    else console.log(r.ok ? 'The bundle may be imported: run again with --apply.' : 'The bundle has errors.');
    process.exitCode = r.ok ? 0 : 1;
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
  console.error(`Unknown command ${cmd}`);
  process.exitCode = 2;
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
