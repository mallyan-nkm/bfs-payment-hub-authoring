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

/** Accepts a payment: pins it to the bank's active distribution in the environment, and returns which. */
export async function accept(rt: RuntimeClient, req: { paymentId: string; bank: string; environment: string }): Promise<string> {
  return (await call(rt, 'POST', '/payments/pin', req)).distribution;
}

/** Records a payment's end: the outcome its root instance ended with. */
export async function end(rt: RuntimeClient, paymentId: string, outcome: string): Promise<boolean> {
  return (await call(rt, 'POST', '/payments/end', { paymentId, outcome })).recorded;
}

/**
 * Keeps a file the front door received for a payment in the runtime's store (Issue I-97, R-09-62): its name, size, direction and
 * kind, signed by the runtime. The file's name is the inbound message's (a `# file: <name>` first line, the samples' metadata), else
 * the message type's.
 */
export async function keepFile(rt: RuntimeClient, f: { tenant: string; paymentId: string; message?: string; payload: string; direction?: 'in' | 'out' }): Promise<{ id: string; sha256: string }> {
  const first = f.payload.split('\n', 1)[0].trim();
  const named = /^#\s*file:\s*(\S+)/.exec(first)?.[1];
  const kind = f.message ?? 'inbound';
  const scheme = kind.split('.').length >= 3 ? kind.split('.').slice(0, 2).join('.') : 'p';
  return call(rt, 'POST', '/payments/files', {
    tenant: f.tenant, scheme, direction: f.direction ?? 'in', kind, fileName: named ?? `${kind}.txt`, paymentId: f.paymentId,
    contentBase64: Buffer.from(f.payload, 'utf8').toString('base64'),
  });
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
}

/** Accepts a payment, runs it from the distribution it is pinned to, and records its end. */
export async function acceptAndRun(req: AcceptAndRun): Promise<{ distribution: string; result: DeployResult; ended: boolean; manifest: any }> {
  const distribution = await accept(req.runtime, { paymentId: req.paymentId, bank: req.bank, environment: req.environment });
  return { distribution, ...(await runPinned({ ...req, distribution })) };
}

/** Runs a payment already pinned (a message for a running payment goes to its own distribution): from that distribution only. */
export async function runPinned(req: AcceptAndRun & { distribution: string }): Promise<{ result: DeployResult; ended: boolean; manifest: any }> {
  const { dir, manifest } = await distributionCopy(req.runtime, req.distribution, req.cache);
  // The file the payment arrived with is kept before it runs, as received.
  if (req.inbound?.payload) await keepFile(req.runtime, { tenant: req.bank, paymentId: req.paymentId, message: req.inbound.message, payload: req.inbound.payload });
  const pkg = path.join(dir, 'engine', req.workflow);
  if (!fs.existsSync(pkg)) throw new Error(`${req.distribution} carries no ${req.workflow}`);
  const result = await deploy({
    packagePath: pkg, workflow: req.workflow, engine: req.engine ?? manifest.engine, environment: { tenant: req.bank, ...req.deployEnvironment },
    inbound: req.inbound, studioRoot: req.studioRoot, distribution: req.distribution, paymentId: req.paymentId,
  });
  const ended = result.outcome ? await end(req.runtime, req.paymentId, result.outcome) : false;
  return { result, ended, manifest };
}
