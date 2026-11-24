// The front door's one-off run of a payment already pinned (deploy/laptop, R-09-80): `run-pinned '<json>'` in the runtime image.
// It takes the front door's client-credentials token from the bank's identity provider, fetches the distribution the payment is
// pinned to from the release API (verified against the trusted signers), runs the payment from that copy on its engine (Temporal
// on the SQL Server plugin by default, R-09-79) with the store on SQL Server, records its end, and prints one JSON line.
//   { paymentId, distribution, bank, environment, workflow, message, sample (a file under /opt/th), date, bench }
// The bank's values are those of the configuration release in force on the payment's date, read from the release API (GET /values,
// R-09-187, Issue I-238), never the bank folder's configuration set; the distribution's own configuration set gives its bindings. The
// message and its file are the caller's to name: there is no scheme's default.
import * as fs from 'node:fs';
import * as path from 'node:path';
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
if (typeof req.message !== 'string' || !req.message) throw new Error('Name the start message (message), as the scheme\'s catalogue names it');
if (typeof req.sample !== 'string' || !req.sample) throw new Error('Name the file the payment arrives with (sample), under /opt/th');
const date = req.date ?? new Date().toISOString().slice(0, 10);
const asOf = `${date}T00:00:00Z`;
const valuesAnswer = await fetch(`${rt.url.replace(/\/$/, '')}/values?tenant=${encodeURIComponent(bank)}&at=${encodeURIComponent(asOf)}`, { headers: rt.headers });
if (!valuesAnswer.ok) throw new Error(`The release API did not give ${bank}'s values in force on ${date}: ${valuesAnswer.status}`);
// Each value in force then, from its configuration release, as of the run's date: later than any the distribution's own set carries.
const values = ((await valuesAnswer.json()) as any[]).map((v) => ({ parameter: v.parameter, value: v.value, validFrom: date, configurationSet: v.configurationSet }));
const payload = fs.readFileSync(path.join(root, req.sample), 'utf8');
const started = Date.now();
const r = await runPinned({
  runtime: rt, bank, environment: req.environment ?? 'test', paymentId: req.paymentId, distribution: req.distribution, workflow: req.workflow ?? 'p.wf.corporate.v1',
  studioRoot: path.join(root, 'studio'), cache, inbound: { message: req.message, payload },
  deployEnvironment: { evaluationDate: date, bench: req.bench ?? [], values }, engine: req.engine,
});
console.log(JSON.stringify({
  paymentId: req.paymentId, outcome: r.result.outcome, values: values.length, ended: r.ended, ms: Date.now() - started,
  distribution: r.result.engine?.distribution, engine: r.manifest?.engine, log: (r.result.log ?? []).slice(-15),
}));
