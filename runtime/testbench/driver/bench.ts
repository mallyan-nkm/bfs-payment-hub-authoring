// The test bench's studio-side driver (runtime/testbench/README.md). TransactHub.TestBench writes a job and
// runs this file with the studio's tsx, from studio/:
//
//   node_modules/.bin/tsx ../runtime/testbench/driver/bench.ts <job.json> <result.json>
//
// It does three things, all with the studio's own code and none changed:
//   1. the reference: the studio interpreter runs the input (runTest, as POST /api/bench/run does);
//   2. the package of each engine: exported from the content branch for the tenant with its simulators, or
//      the package the job names (a stored release or an exported zip), with the bank's simulators added to
//      its workers when it is promotable and carries none;
//   3. each engine: the deploy runner (studio/src/deploy/deploy.ts), with the environment the job gives that
//      engine (the store's provider and connection, the Flowable REST server, the Temporal address). The deploy
//      runner starts one worker per task queue and runs the engine's harness, as `studio deploy` does.
// It reads the content branch (TH_CONTENT_REPO) or the in-memory fake seeded from content-configurations/.

import * as fs from 'node:fs';
import * as path from 'node:path';
import { openReadEnv } from '../../../studio/src/env.js';
import { runTest } from '../../../studio/src/testing/runner.js';
import { exportPackage, packageHash, signedZip } from '../../../studio/src/export/exporter.js';
import { claimsOf, generateSigningKey, signFiles, trustedSignersJson } from '../../../studio/src/export/signing.js';
import { verifyPackageDir } from '../../../studio/src/deploy/deploy.js';
import { createPrivateKey } from 'node:crypto';
import { deploy, type Engine } from '../../../studio/src/deploy/deploy.js';
import { unzip } from '../../../studio/src/export/zip.js';
import { parseYaml, toYaml } from '../../../studio/src/core/yaml.js';

interface Job {
  workflow: string;
  tenant?: string;
  banks?: string[];
  evaluationDate: string;
  /** When the run starts that day, HH:MM:SS UTC (clock.time; default 06:00:00). */
  startTime?: string;
  /** Values the request gives beside the bank's configuration, each with its validFrom (values). */
  values?: { parameter: string; value: any; validFrom: string }[];
  inbound?: { payload: string; message?: string };
  context?: any;
  bench: { port: string; simulator: string; scenario: string }[];
  reference: boolean;
  /** The release the content branch must hold for the workflow, when the request names one. */
  release?: string;
  /** Packages by engine, when the job does not export from the content branch. */
  packages?: Record<string, string>;
  engines: { engine: Engine; env: Record<string, string> }[];
  work: string;
}

const [jobFile, outFile] = process.argv.slice(2);
if (!jobFile || !outFile) {
  console.error('usage: bench.ts <job.json> <result.json>');
  process.exit(2);
}
const job: Job = JSON.parse(fs.readFileSync(jobFile, 'utf8'));
const OWNER = 'owner';
const progress = (what: string) => console.log(`PROGRESS ${JSON.stringify(what)}`);

/** The trace of one item in a batch of one, written without its index (as the studio's test runner does). */
function normalise(trace: string[] = []) {
  return trace.some((t) => /\[[1-9]\d*\]:/.test(t)) ? trace : trace.map((t) => t.replace(/\[0\]:/, ':'));
}

/** The level the studio's bench gives a target (studio/src/server/api.ts, benchRun). */
function levelOf(doc: any, inbound: boolean) {
  if (doc.kind === 'activity') return 'activity';
  if (doc.kind === 'hook') return 'hook';
  if (doc.wfKind === 'handler') return 'handler';
  if (inbound && job.inbound?.message) return 'end-to-end';
  if (doc.wfKind === 'leg') return 'leg';
  if (doc.wfKind === 'platform') return inbound ? 'end-to-end' : 'leg';
  return 'activity';
}

/**
 * A promotable package carries no simulator (R-07-12). For a test run only, each worker of an unpacked copy
 * gets the simulators of the ports it reaches, from the content branch; the package itself is not changed.
 */
function withSimulators(env: any, zipFile: string, dest: string): { dir: string; added: string[]; verified: Promise<any> } {
  fs.mkdirSync(dest, { recursive: true });
  if (fs.statSync(zipFile).isDirectory()) fs.cpSync(zipFile, dest, { recursive: true });
  else for (const e of unzip(fs.readFileSync(zipFile))) {
    const f = path.join(dest, e.name);
    fs.mkdirSync(path.dirname(f), { recursive: true });
    fs.writeFileSync(f, e.content);
  }
  // The package as released is verified first, against the deployment's trusted signers (R-09-26).
  const verified = verifyNow(dest);
  const manifest = parseYaml(fs.readFileSync(path.join(dest, 'manifest.yaml'), 'utf8'));
  const added: string[] = [];
  for (const w of manifest.workers ?? []) {
    const wdir = path.join(dest, w.path);
    const portDir = path.join(wdir, 'artefacts', 'port');
    const ports = new Set<string>();
    if (fs.existsSync(portDir)) for (const f of fs.readdirSync(portDir)) ports.add(parseYaml(fs.readFileSync(path.join(portDir, f), 'utf8')).name);
    const simDir = path.join(wdir, 'simulators');
    if (fs.existsSync(simDir) && fs.readdirSync(simDir).length) continue;
    for (const s of env.store.byKind('simulator')) {
      if (!ports.has(s.doc.port)) continue;
      fs.mkdirSync(simDir, { recursive: true });
      fs.writeFileSync(path.join(simDir, `${s.doc.name}@${s.doc.release}.yaml`), toYaml(s.doc));
      added.push(`${w.queue}:${s.doc.name}`);
    }
  }
  // The test copy is no longer the released package: the bench signs it with its own key, trusted for this run only.
  if (added.length) {
    const files: Record<string, Buffer> = {};
    const walk = (d: string) => { for (const x of fs.readdirSync(d, { withFileTypes: true })) { const f = path.join(d, x.name); if (x.isDirectory()) walk(f); else files[path.relative(dest, f).split(path.sep).join('/')] = fs.readFileSync(f); } };
    walk(dest);
    delete files['release.json'];
    delete files['release.sig'];
    delete files['package.sha256'];
    const signed = signFiles(files, claimsOf(manifest), createPrivateKey(benchKey().privateKeyPem));
    for (const [n, c] of Object.entries(signed)) if (n === 'release.json' || n === 'release.sig') fs.writeFileSync(path.join(dest, n), c);
  }
  return { dir: dest, added, verified };
}

/**
 * The bench's own signing key, made for this run in its work folder and gone with it: it signs what the bench exports
 * from the content branch, and a test copy it changed. Used only when the environment names no TH_SIGNING_KEY.
 */
let key: { privateKeyPem: string; publicKeyPem: string; keyId: string; trust: string } | undefined;
function benchKey() {
  if (key) return key;
  const k = generateSigningKey();
  const dir = path.join(job.work, 'signing');
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'key.pem'), k.privateKeyPem, { mode: 0o600 });
  const trust = path.join(dir, 'trusted-signers.json');
  fs.writeFileSync(trust, trustedSignersJson([k]));
  key = { ...k, trust };
  return key;
}

/** A released package's signature checked against TH_TRUSTED_SIGNERS; without one configured, said so. */
async function verifyNow(dir: string) {
  if (!process.env.TH_TRUSTED_SIGNERS) return { verified: false, note: 'TH_TRUSTED_SIGNERS is not set: the package as released was not verified' };
  const v = await verifyPackageDir(dir);
  return { verified: true, keyId: v.keyId, files: v.files };
}

async function main() {
  const t0 = Date.now();
  const { env, where, stop } = await openReadEnv(job.banks?.length ? { banks: job.banks } : {});
  const out: any = { content: where, engines: [] };
  try {
    const latest = env.store.latest(job.workflow);
    if (!latest) throw new Error(`${job.workflow} is not published on ${where}`);
    out.workflow = { name: job.workflow, release: latest.doc.release };
    if (job.release && !job.packages && latest.doc.release !== job.release) throw new Error(`${where} holds ${job.workflow}@${latest.doc.release}, not the release ${job.release} the request names`);
    progress(`content ${where}: ${job.workflow}@${latest.doc.release}`);

    // 1. The reference: the studio interpreter.
    if (job.reference) {
      const r0 = Date.now();
      const test = {
        kind: 'test', id: 'TESTBENCH', title: 'Test bench reference', target: job.workflow, level: levelOf(latest.doc, !!job.inbound),
        evaluationDate: job.evaluationDate, tenant: job.tenant && job.tenant !== OWNER ? job.tenant : undefined, bench: job.bench,
        given: { ...(job.context ? { context: job.context } : {}), ...(job.inbound?.message ? { message: job.inbound.message } : {}) },
        expect: {},
      };
      const r = await runTest(env, test, { ...(job.inbound ? { inbound: job.inbound } : {}), ...(job.startTime ? { startTime: job.startTime } : {}), ...(job.values?.length ? { values: job.values } : {}) } as any);
      out.reference = { engine: 'interpreter', ok: !r.error, error: r.error?.split('\n')[0], outcome: r.outcome, trace: normalise(r.trace), detail: normalise(r.detail), elapsedMs: Date.now() - r0, businessElapsed: r.elapsed, warnings: r.warnings, level: test.level };
      progress(`interpreter: ${r.outcome ?? r.error?.split('\n')[0]}`);
    }

    // 2 and 3. Each engine: its package, then the deploy runner. Every package is signed (R-09-23) and verified before it
    // runs (R-09-26): with no TH_SIGNING_KEY the bench signs its exports with its own key and trusts that key alone.
    if (!process.env.TH_SIGNING_KEY) {
      process.env.TH_SIGNING_KEY = `file:${path.join(job.work, 'signing', 'key.pem')}`;
      benchKey();
      process.env.TH_TRUSTED_SIGNERS ??= `file:${benchKey().trust}`;
    }
    const base = { ...process.env };
    for (const e of job.engines) {
      const r0 = Date.now();
      const res: any = { engine: e.engine, ok: false };
      out.engines.push(res);
      try {
        progress(`${e.engine}: package`);
        let pkgPath: string;
        if (job.packages) {
          const given = job.packages[e.engine];
          if (!given) throw new Error(`No package for ${e.engine}: the request names packages for ${Object.keys(job.packages).join(', ') || 'no engine'}`);
          const ws = withSimulators(env, given, path.join(job.work, `${e.engine}-package`));
          pkgPath = ws.dir;
          res.package = { from: path.basename(given), verified: ws.verified, simulatorsAdded: ws.added };
          if (ws.added.length) res.package.resignedForTheTest = benchKey().keyId;
        } else {
          const pkg = exportPackage(env, { workflows: [job.workflow], target: e.engine, withSimulators: true, tenant: job.tenant && job.tenant !== OWNER ? job.tenant : undefined });
          pkgPath = path.join(job.work, `${e.engine}.zip`);
          fs.writeFileSync(pkgPath, signedZip(pkg));
          res.package = { from: 'content', hash: packageHash(pkg), workers: pkg.manifest.workers.map((w: any) => w.queue) };
        }
        if (res.package?.verified) res.package.verified = await res.package.verified;
        progress(`${e.engine}: running`);
        // The harness the deploy runner starts inherits this process's environment.
        for (const k of Object.keys(process.env)) if (!(k in base)) delete process.env[k];
        Object.assign(process.env, base, e.env);
        if (res.package?.resignedForTheTest) process.env.TH_TRUSTED_SIGNERS = `file:${benchKey().trust}`;
        const d = await deploy({
          packagePath: pkgPath, workflow: job.workflow, studioRoot: env.studioRoot,
          environment: { evaluationDate: job.evaluationDate, tenant: job.tenant && job.tenant !== OWNER ? job.tenant : undefined, bench: job.bench, ...(job.startTime ? { startTime: job.startTime } : {}), ...(job.values?.length ? { values: job.values } : {}) },
          inbound: job.inbound, context: job.context,
        });
        Object.assign(res, {
          ok: true, outcome: d.outcome, failed: d.failed, trace: normalise(d.trace), detail: normalise(d.detail), attempts: d.attempts,
          intake: d.intake, workers: d.workers.map((w) => ({ queue: w.queue, served: w.served, refused: w.refused })),
          messages: (d.messages ?? []).map((m: any) => ({ id: m.id, type: m.type })),
          store: d.engine?.store, engineInfo: { ...(d.engine ?? {}), store: undefined }, harnessMs: d.elapsedMs, log: d.log,
        });
      } catch (err: any) {
        res.error = String(err?.message ?? err).slice(0, 4000);
      } finally {
        for (const k of Object.keys(process.env)) if (!(k in base)) delete process.env[k];
        Object.assign(process.env, base);
        res.elapsedMs = Date.now() - r0;
        progress(`${e.engine}: ${res.outcome ?? 'error'}`);
      }
    }
  } catch (err: any) {
    out.error = String(err?.message ?? err);
  } finally {
    out.elapsedMs = Date.now() - t0;
    fs.writeFileSync(outFile, JSON.stringify(out, null, 2));
    await stop();
  }
}

main().then(() => process.exit(0), (e) => {
  console.error(e?.stack ?? e);
  process.exit(1);
});
