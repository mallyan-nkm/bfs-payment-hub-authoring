import { CONTENT_DIR, openReadEnv, type StudioEnv } from './env.js';
import { runAllTests, runTest } from './testing/runner.js';
import { initTelemetry, shutdownTelemetry } from './telemetry/telemetry.js';
import { api, CliAuthError, login, logout, serverOf } from './cli-auth.js';

// Command line: `npm run studio -- test [ID...]`, `kit`, `check`, `export <workflow> <target>`.
// `--bank <name>` (repeatable, any command) adds a bank pack from its folder on the branch (Issue I-43).
//
// Content (R-11-21): the connected branch when TH_CONTENT_REPO is set (with TH_CONTENT_BRANCH,
// TH_GITHUB_TOKEN, TH_GITHUB_API); otherwise an in-memory fake GitHub seeded from this repository's
// content-configurations/. Either way nothing is written to disk, and the command says which it read.
//
// Sign-in (R-11-46, Issue I-71): `login --server <url>` signs the command line in to a studio by device
// authorization. With `--server` (or TH_STUDIO_URL), `import` and `whoami` call that studio's API as the
// signed-in user, and the permission table decides as it does for the web app; `--actor` is refused
// there. Applying an import to a connected branch without a studio is refused: nothing proves who typed it.

const USAGE = 'Usage: studio [--bank <name> ...] login --server <url> | logout [--server <url>] | whoami [--server <url>] | test [ID...] | kit [CK-ID...] | check | seed-content [--actor a] [--write-local] | bank-pack <name> [--licensed <scheme> ...] [--release <r>] [--write] | export <workflow> <target> | export-zip <workflow> <target> <out.zip> [--with-simulators] [--tenant t] | signing-key <dir> | import <bundle.zip|dir> [--server <url>] [--tenant t] [--bank-tenant b] [--actor a] [--bank-actor x] [--apply] [--json] | import-sample <out.zip> | deploy <package> --workflow <name> [--inbound file [--message <start message>]] [--date YYYY-MM-DD] [--tenant t] [--config configuration-set.yaml] [--value parameter=value ...] [--bench port=simulator:scenario | port=runtime ...]';

/** A mistake in the command line: the usage and exit code 2. */
class UsageError extends Error {}

const COMMANDS = ['login', 'logout', 'whoami', 'test', 'kit', 'check', 'seed-content', 'bank-pack', 'export', 'export-zip', 'import', 'import-sample', 'deploy', 'signing-key'];

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
  if (cmd === 'login' || cmd === 'logout' || cmd === 'whoami' || (cmd === 'import' && serverOf(args))) {
    const server = serverOf(args);
    if (!server) throw new UsageError(`${cmd} needs the studio: --server <url> or TH_STUDIO_URL`);
    if (cmd === 'login') {
      const t = await login(server);
      console.log(`signed in to ${server} as ${t.user} (${t.tenant}): ${(t.roles ?? []).join(', ') || 'no roles'}; until ${t.expiresAt}`);
    } else if (cmd === 'logout') {
      await logout(server);
      console.log(`signed out of ${server}`);
    } else if (cmd === 'whoami') {
      const m = await api(server, 'GET', '/api/me');
      console.log(`${m.user} (${m.tenant}): ${m.roles.join(', ') || 'no roles'}`);
    } else await remoteImport(server, args);
    return;
  }
  if (cmd === 'seed-content' && args.includes('--write-local')) {
    // Regenerate this repository's content-configurations/ as the seed publishes it: the seed runs as a
    // change set against an in-memory fake seeded from the folder, and the fake's tree is copied back.
    const { writeLocalSeed } = await import('./content/seed-local.js');
    const r = await writeLocalSeed(opt(args, 'actor'));
    console.log(r.status === 'unchanged' ? `content-configurations/ is current: ${r.unchanged} seeded files, nothing to write` : `content-configurations/: ${r.written.length} file(s) written by change set ${r.changeSet}${r.replaced.length ? `; ${r.replaced.join(', ')} replaced by a later release, the earlier kept` : ''}`);
    return;
  }
  if (cmd === 'signing-key') {
    // A P-256 signing key for a first installation or a local run (R-09-23): the private key, readable by its owner only,
    // and the trusted-signers document that lists its public key. Point TH_SIGNING_KEY at the one (file:<dir>/signing-key.pem)
    // and TH_TRUSTED_SIGNERS at the other. Never commit the key.
    const fs = await import('node:fs');
    const path = await import('node:path');
    if (!args[0]) throw new UsageError('signing-key needs the folder to write the key to');
    const { generateSigningKey, trustedSignersJson } = await import('./export/signing.js');
    const k = generateSigningKey();
    fs.mkdirSync(args[0], { recursive: true });
    const keyFile = path.resolve(args[0], 'signing-key.pem');
    if (fs.existsSync(keyFile)) throw new UsageError(`${keyFile} exists: a key is never overwritten`);
    fs.writeFileSync(keyFile, k.privateKeyPem, { mode: 0o600 });
    fs.writeFileSync(path.resolve(args[0], 'trusted-signers.json'), trustedSignersJson([k]));
    console.log(`key ${k.keyId}\nTH_SIGNING_KEY=file:${keyFile}\nTH_TRUSTED_SIGNERS=file:${path.resolve(args[0], 'trusted-signers.json')}`);
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
    // studio bank-pack <name> [--licensed <scheme> ...] [--release <r>] [--write]: the lock file of a bank pack, as
    // tools/build_factory.py writes the example bank's. It names the schemes the bank licenses (R-11-37): --licensed, else the
    // list the lock file holds. A new --release keeps the earlier lock file at packs/<release>.yaml, as a seed does (R-11-35).
    need(args[0] && !args[0].startsWith('--'), 'bank-pack needs a bank: a folder name under content-configurations/banks');
    const fs = await import('node:fs');
    const path = await import('node:path');
    const { packManifest } = await import('./governance/packs.js');
    const { toYaml } = await import('./core/yaml.js');
    const { SEED_HEADER } = await import('./core/store.js');
    const tenant = [...env.store.bankPacks].find(([, folder]) => folder === args[0])?.[0];
    if (!tenant) throw new Error(`No bank pack ${args[0]} is loaded: pass --bank ${args[0]}`);
    const licensed = args.flatMap((a, i) => (args[i - 1] === '--licensed' ? [a] : []));
    const { compareReleases } = await import('./core/names.js');
    const release = opt(args, 'release');
    const manifest = packManifest(env, 'b', tenant, { ...(licensed.length ? { licensed } : {}), ...(release ? { release } : {}) });
    const unknown = manifest.licensedSchemes.filter((s: string) => !env.store.byKind('scheme').some((e) => e.doc.scope === s));
    if (unknown.length) throw new UsageError(`--licensed ${unknown.join(', ')}: no such scheme in the factory`);
    const text = '# Generated by `npm run studio -- bank-pack`. The lock file of this pack.\n' + toYaml(manifest);
    if (args.includes('--write')) {
      // A repository authoring step, like tools/build_factory.py: it writes this repository's folder, not the studio's store.
      const file = path.join(CONTENT_DIR, 'banks', args[0], 'pack.yaml');
      const before = fs.readFileSync(file, 'utf8');
      if (SEED_HEADER.test(before)) throw new Error(`${file} is seeded from spec/: run tools/build_factory.py and seed again`);
      const { parseYaml } = await import('./core/yaml.js');
      const old = parseYaml(before);
      if (old?.release && old.release !== manifest.release) {
        if (compareReleases(manifest.release, old.release) <= 0) throw new UsageError(`--release ${manifest.release} is not later than ${old.release}`);
        const kept = path.join(CONTENT_DIR, 'banks', args[0], 'packs', `${old.release}.yaml`);
        fs.mkdirSync(path.dirname(kept), { recursive: true });
        if (!fs.existsSync(kept)) fs.writeFileSync(kept, before);
        console.log(`${kept}: the earlier lock file, kept`);
      } else if (before !== text && old?.release === manifest.release && !args.includes('--force')) {
        throw new UsageError(`The lock file of ${args[0]} changes at release ${manifest.release}: give it a later --release (R-11-35), or --force for a lock file not yet published`);
      }
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
    // With a studio named, the signed-in user must hold the right to deploy (R-11-46): the table decides, not the command line.
    const server = serverOf(args);
    if (server) {
      const p = await api(server, 'GET', `/api/permit?action=run&kind=deploy&scope=${opt('tenant') && opt('tenant') !== 'owner' ? 'b' : 'p'}`);
      if (!p.allowed) throw new CliAuthError(`Not allowed to deploy: ${p.reason}`);
    }
    const bench = args.flatMap((a, i) => (args[i - 1] === '--bench' ? [a] : [])).map((b) => {
      // port=runtime: the runtime's native host answers the port, through the bank's port binding (R-08-15).
      const r = /^([^=]+)=runtime$/.exec(b);
      if (r) return { port: r[1], simulator: 'runtime', scenario: '' };
      const m = /^([^=]+)=([^:]+):(.+)$/.exec(b);
      need(m, `--bench ${b}: write it as port=simulator:scenario or port=runtime`);
      return { port: m![1], simulator: m![2], scenario: m![3] };
    });
    const tenant = opt('tenant');
    // --value parameter=value: a value in force from the run's date, on top of the configuration set (a test's short wait, say).
    const date = opt('date') ?? '2026-11-17';
    const values = args.flatMap((a, i) => (args[i - 1] === '--value' ? [a] : [])).map((v) => {
      const m = /^([^=]+)=(.+)$/.exec(v);
      need(m, `--value ${v}: write it as parameter=value`);
      return { parameter: m![1], value: m![2], validFrom: date };
    });
    const r = await deploy({
      packagePath: args[0],
      workflow: opt('workflow')!,
      studioRoot: env.studioRoot,
      environment: { evaluationDate: date, tenant, bench, ...(values.length ? { values } : {}), configurationSet: opt('config') ? (await import('./core/yaml.js')).parseYaml((await import('node:fs')).readFileSync(opt('config')!, 'utf8')) : undefined },
      inbound: opt('inbound') ? { payload: (await import('node:fs')).readFileSync(opt('inbound')!, 'utf8'), ...(opt('message') ? { message: opt('message') } : {}) } : undefined,
    });
    for (const l of r.log) console.log(l);
    for (const w of r.workers) console.log(`worker ${w.queue} (pid ${w.pid}) served ${JSON.stringify(w.served)}, refused ${w.refused}${w.valuesRead ? `, values read from the runtime ${w.valuesRead} time(s)` : ''}`);
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
    // On a connected branch, an import is applied only through a studio, as a signed-in user (R-11-46).
    if (args.includes('--apply') && !local) throw new UsageError(`Applying an import to ${where} needs a signed-in user: run \`login --server <url>\` and pass --server <url>`);
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
    const { exportPackage, signPackage, exportZip } = await import('./export/exporter.js');
    const { primeSigningKey } = await import('./export/signing.js');
    await primeSigningKey();
    // Signed with the studio's key (R-09-23), as every release is: a deployment takes no unsigned package.
    const pkg = signPackage(exportPackage(env, { workflows: [args[0]], target: args[1] as any, withSimulators: args.includes('--with-simulators'), tenant: args.includes('--tenant') ? args[args.indexOf('--tenant') + 1] : undefined }));
    (await import('node:fs')).writeFileSync(args[2], exportZip(pkg));
    console.log(`${args[2]}: ${Object.keys(pkg.files).length} files, workers ${pkg.manifest.workers.map((w: any) => w.queue).join(', ')}, signed by key ${JSON.parse(pkg.files['release.json']).keyId}`);
    return;
  }
  console.error(`Unknown command ${cmd}`);
  process.exitCode = 2;
  console.log(USAGE);
}

/** `import --server`: the studio analyses or applies the bundle, as the signed-in user, under the permission table (R-11-46). */
async function remoteImport(server: string, args: string[]) {
  const fs = await import('node:fs');
  const path = await import('node:path');
  const opt = (k: string) => { const i = args.indexOf(`--${k}`); return i >= 0 ? args[i + 1] : undefined; };
  if (opt('actor') || opt('bank-actor')) throw new UsageError('With --server the acting user is the signed-in one: --actor and --bank-actor are refused (R-11-46)');
  const src = args.find((a, i) => !a.startsWith('--') && !['server', 'tenant', 'bank-tenant'].includes(args[i - 1]?.replace(/^--/, '')));
  if (!src) throw new UsageError('import needs a bundle: a .zip or a directory');
  let zipped: Buffer;
  if (fs.statSync(src).isDirectory()) {
    const { bundleZip } = await import('./import/sample.js');
    const files: { name: string; content: string }[] = [];
    const walk = (d: string) => {
      for (const e of fs.readdirSync(d, { withFileTypes: true })) {
        const f = path.join(d, e.name);
        if (e.isDirectory()) walk(f);
        else files.push({ name: path.relative(src, f).split(path.sep).join('/'), content: fs.readFileSync(f, 'utf8') });
      }
    };
    walk(src);
    zipped = bundleZip(files);
  } else zipped = fs.readFileSync(src);
  const body = { bundle: zipped.toString('base64'), ...(opt('bank-tenant') ? { bankTenant: opt('bank-tenant') } : {}), ...(args.includes('--force') ? { force: true } : {}) };
  const out = await api(server, 'POST', args.includes('--apply') ? '/api/import/apply' : '/api/import/preview', body);
  const r = out.report ?? out;
  if (args.includes('--json')) console.log(JSON.stringify(out, null, 2));
  else if (out.applied) console.log(`saved    ${out.saved.length} as drafts on ${server}; ${out.skipped.length} unchanged and reused\nNothing is published: a checker approves each change set in Governance.`);
  else console.log(r.ok ? `The bundle may be imported${args.includes('--apply') ? '' : ': run again with --apply'}.` : 'The bundle has errors.');
  process.exitCode = r.ok ? 0 : 1;
}

// Telemetry over OTLP when OTEL_* names an endpoint (R-10-07): a deploy is one trace across every process it starts.
initTelemetry('transacthub-studio-cli');
main().catch((e) => {
  if (e instanceof CliAuthError) {
    console.error(e.message);
    process.exitCode = 1;
    return;
  }
  if (e instanceof UsageError) {
    console.error(e.message);
    console.error(USAGE);
    process.exitCode = 2;
    return;
  }
  console.error(e?.message ?? e);
  process.exitCode = 1;
}).finally(() => shutdownTelemetry());
