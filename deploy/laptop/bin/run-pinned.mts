// The front door's one-off run of a payment already pinned (deploy/laptop, R-09-80): `run-pinned '<json>'` in the runtime image.
// It takes the front door's client-credentials token from the bank's identity provider, fetches the distribution the payment is
// pinned to from the release API (verified against the trusted signers), runs the payment from that copy on its engine (Temporal
// on the SQL Server plugin by default, R-09-79) with the store on SQL Server, records its end, and prints one JSON line.
//   { paymentId, distribution, bank, environment, workflow, message, sample (a file under content-configurations/), date, bench }
import * as fs from 'node:fs';
import * as path from 'node:path';
import { parseYaml } from '../../../studio/src/core/yaml.js';
import { runPinned } from '../../../studio/src/deploy/front-door.js';

const req = JSON.parse(process.argv[2] ?? '{}');
const env = (k: string) => process.env[k] ?? (() => { throw new Error(`Set ${k}`); })();
const token = async () => {
  const r = await fetch(env('TH_IDP_TOKEN_URL'), {
    method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'client_credentials', client_id: 'th-front-door', client_secret: env('TH_LAPTOP_FRONTDOOR_SECRET') }),
  });
  if (!r.ok) throw new Error(`The bank's identity provider refused the front door: ${r.status}`);
  return (await r.json()).access_token as string;
};

const root = '/opt/th';
const bank = req.bank ?? env('TH_BANK');
const cache = path.join(process.env.HOME ?? '/tmp', 'distributions');
const rt = { url: env('TH_RUNTIME_API'), headers: { authorization: `Bearer ${await token()}` } };
const configSet = parseYaml(fs.readFileSync(path.join(root, 'content-configurations', 'banks', bank, 'configuration-set.yaml'), 'utf8'));
const payload = fs.readFileSync(path.join(root, req.sample ?? 'studio/fixtures/samples/wps-sif-valid.sif'), 'utf8');
const started = Date.now();
const r = await runPinned({
  runtime: rt, bank, environment: req.environment ?? 'test', paymentId: req.paymentId, distribution: req.distribution, workflow: req.workflow ?? 'p.wf.corporate.v1',
  studioRoot: path.join(root, 'studio'), cache, inbound: { message: req.message ?? 'uae.wps.sif', payload },
  deployEnvironment: { evaluationDate: req.date ?? '2026-11-24', bench: req.bench ?? [], configurationSet: configSet }, engine: req.engine,
});
console.log(JSON.stringify({
  paymentId: req.paymentId, outcome: r.result.outcome, ended: r.ended, ms: Date.now() - started,
  distribution: r.result.engine?.distribution, engine: r.manifest?.engine, log: (r.result.log ?? []).slice(-15),
}));
