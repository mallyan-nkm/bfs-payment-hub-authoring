import * as fs from 'node:fs';
import * as path from 'node:path';
import { parseYaml } from '../core/yaml.js';
import { unzip } from '../export/zip.js';
import { trustedSigners, verifyFiles } from '../export/signing.js';
import { deploy, type DeployEnvironment, type DeployResult, type Engine } from './deploy.js';

// The runtime's front door for a bank's distributions (docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 5, R-09-53 to
// R-09-55). At acceptance it asks the runtime to pin the payment to the distribution active for its bank and environment
// then; it runs the payment from that distribution's own copy (fetched from the runtime once, verified against the
// trusted signers, kept side by side with every other distribution it has run), with the engine naming its queues,
// orchestrations, definitions or deployment after the distribution; when the root instance ends it records the payment's
// end on the runtime (Issue I-97). A payment pinned to a distribution never runs from another.

export interface RuntimeClient { url: string; headers: Record<string, string> }

async function call(rt: RuntimeClient, method: string, p: string, body?: any): Promise<any> {
  const r = await fetch(`${rt.url}${p}`, { method, headers: { ...rt.headers, ...(body ? { 'content-type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
  const text = await r.text();
  if (!r.ok) throw new Error(`${method} ${p} answered ${r.status}: ${text.slice(0, 500)}`);
  return text ? JSON.parse(text) : undefined;
}

/** A payment the runtime refused because its scheme's intake is paused for the bank (R-09-62). */
export class IntakePaused extends Error {
  readonly code = 'ops.IntakePaused';
}

/**
 * Accepts a payment: pins it to the bank's active distribution in the environment, and returns which. With the scheme, the
 * runtime first reads the scheme's intake switch (R-09-62): while it is paused the payment is refused, or, with `queue`, kept here
 * and offered again every `every` ms until intake is resumed or `queue` ms have passed.
 */
export async function accept(rt: RuntimeClient, req: { paymentId: string; bank: string; environment: string; scheme?: string }, wait?: { queue: number; every?: number }): Promise<string> {
  const until = Date.now() + (wait?.queue ?? 0);
  for (;;) {
    const r = await fetch(`${rt.url}/payments/pin`, { method: 'POST', headers: { ...rt.headers, 'content-type': 'application/json' }, body: JSON.stringify(req) });
    const text = await r.text();
    if (r.status === 423) {
      if (!wait || Date.now() >= until) throw new IntakePaused(JSON.parse(text).error);
      await new Promise((res) => setTimeout(res, wait.every ?? 1000));
      continue;
    }
    if (!r.ok) throw new Error(`POST /payments/pin answered ${r.status}: ${text.slice(0, 500)}`);
    return JSON.parse(text).distribution;
  }
}

/** Records a payment's end: the outcome its root instance ended with. */
export async function end(rt: RuntimeClient, paymentId: string, outcome: string): Promise<boolean> {
  return (await call(rt, 'POST', '/payments/end', { paymentId, outcome })).recorded;
}

/** The local copy of a distribution, side by side with the others under `cache`: fetched once from the runtime and verified. */
export async function distributionCopy(rt: RuntimeClient, distribution: string, cache: string): Promise<{ dir: string; manifest: any }> {
  const dir = path.join(cache, distribution.replace(/[^A-Za-z0-9._-]+/g, '_'));
  const bank = distribution.slice('dist/'.length).split('@')[0];
  if (!fs.existsSync(path.join(dir, 'manifest.yaml'))) {
    const r = await fetch(`${rt.url}/distributions/package?id=${encodeURIComponent(distribution)}`, { headers: rt.headers });
    if (!r.ok) throw new Error(`The runtime gave no package of ${distribution}: ${r.status}`);
    const files: Record<string, Buffer> = {};
    for (const e of unzip(Buffer.from(await r.arrayBuffer()))) files[e.name] = e.content;
    // Verified before anything of it runs (R-09-26): the statement names this distribution, for this bank.
    verifyFiles(files, { releaseId: distribution, tenant: bank, kind: 'distribution' }, await trustedSigners());
    const tmp = `${dir}.part`;
    fs.rmSync(tmp, { recursive: true, force: true });
    // Kept: the manifest and the engine packages it runs (each signed, and verified again when deployed); the rest of the
    // verified distribution is not needed to run a payment, and stays in the runtime's store.
    for (const [n, c] of Object.entries(files).filter(([n]) => n === 'manifest.yaml' || n.startsWith('engine/'))) {
      const f = path.join(tmp, n);
      fs.mkdirSync(path.dirname(f), { recursive: true });
      fs.writeFileSync(f, c);
    }
    fs.renameSync(tmp, dir);
  }
  return { dir, manifest: parseYaml(fs.readFileSync(path.join(dir, 'manifest.yaml'), 'utf8')) };
}

export interface AcceptAndRun {
  runtime: RuntimeClient;
  bank: string;
  environment: string;
  paymentId: string;
  /** The platform workflow the payment runs (its engine package is the distribution's `engine/<workflow>/`). */
  workflow: string;
  inbound?: { message?: string; payload: string };
  deployEnvironment: DeployEnvironment;
  studioRoot: string;
  cache: string;
  engine?: Engine;
  /** The payment's scheme, for its intake switch (R-09-62); by default the scope of the inbound message. */
  scheme?: string;
  /** While the scheme's intake is paused: keep the payment and offer it again, for up to this many ms; by default it is refused. */
  queueWhilePaused?: number;
}

/** Accepts a payment, runs it from the distribution it is pinned to, and records its end. */
export async function acceptAndRun(req: AcceptAndRun): Promise<{ distribution: string; result: DeployResult; ended: boolean; manifest: any }> {
  const scheme = req.scheme ?? req.inbound?.message?.split('.').slice(0, 2).join('.');
  const distribution = await accept(req.runtime, { paymentId: req.paymentId, bank: req.bank, environment: req.environment, ...(scheme ? { scheme } : {}) },
    req.queueWhilePaused ? { queue: req.queueWhilePaused, every: 500 } : undefined);
  return { distribution, ...(await runPinned({ ...req, distribution })) };
}

/** Runs a payment already pinned (a message for a running payment goes to its own distribution): from that distribution only. */
export async function runPinned(req: AcceptAndRun & { distribution: string }): Promise<{ result: DeployResult; ended: boolean; manifest: any }> {
  const { dir, manifest } = await distributionCopy(req.runtime, req.distribution, req.cache);
  const pkg = path.join(dir, 'engine', req.workflow);
  if (!fs.existsSync(pkg)) throw new Error(`${req.distribution} carries no ${req.workflow}`);
  const result = await deploy({
    packagePath: pkg, workflow: req.workflow, engine: req.engine ?? manifest.engine, environment: { tenant: req.bank, ...req.deployEnvironment },
    inbound: req.inbound, studioRoot: req.studioRoot, distribution: req.distribution, paymentId: req.paymentId,
  });
  const ended = result.outcome ? await end(req.runtime, req.paymentId, result.outcome) : false;
  return { result, ended, manifest };
}
