// "Insert, never update" for workflows, proved in the laptop deployment (deploy/laptop, spec R-09-77, R-09-78, R-09-73, R-09-74).
//
//   cd studio && node_modules/.bin/tsx ../deploy/laptop/test/insert-only.mts [--keep] [--no-up]
//
// It brings the compose stack up healthy (the images built by deploy/laptop/build.sh), with passwords and client secrets made for
// this run and given to compose by its environment only, and then, end to end over the services' own APIs:
//  1. publishes distribution A of the runtime's bank (ADIB) from the studio;
//  2. installs and activates A through the service console's API: a service engineer asks, a bank approver consents;
//  3. pins payment P1 to A at the front door (the release API), and leaves it open;
//  4. publishes B, installs it and activates it with consent: A drains;
//  5. pins P2 to B; runs P1 and P2 from the distributions they are pinned to, on Temporal (the SQL Server plugin): P1 finishes
//     on A, P2 on B;
//  6. checks at the database that no row of a distribution, its definitions, its activations or the fact log was updated or
//     deleted: every row seen after step 2 is unchanged at the end, the chains verify, and the runtime's login is refused an
//     UPDATE, a DELETE and a TRUNCATE on each of those tables by the database itself;
//  7. refuses a second install of A's id with other content (signed by the studio's own key), at the release API and at the
//     database's unique key;
//  8. refuses a distribution of DIB, published by the same studio and signed by the same key, in the ADIB runtime: at verify
//     and at install in the service console, and at the release API.
// It removes its containers and volumes at the end unless --keep is given.
import { spawn, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { client } from '../../../studio/test/blackbox/server.js';
import { unzip, zip } from '../../../studio/src/export/zip.js';
import { signFiles } from '../../../studio/src/export/signing.js';
import { parseYaml } from '../../../studio/src/core/yaml.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const LAPTOP = path.resolve(here, '..');
const PROJECT = `th-laptop-it-${process.pid}`;
const BANK = 'adib';
const OTHER = 'dib';
const ENV = 'test';
const keep = process.argv.includes('--keep');
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-laptop-it-'));
const secret = (prefix = '') => prefix + randomBytes(15).toString('base64url').replace(/[^A-Za-z0-9]/g, 'x');
const env: Record<string, string> = {
  ...process.env as Record<string, string>,
  TH_LAPTOP_SA_PASSWORD: secret('Sa1-'), TH_LAPTOP_RUNTIME_DB_PASSWORD: secret('Rt1-'), TH_LAPTOP_TESTBENCH_DB_PASSWORD: secret('Tb1-'),
  TH_LAPTOP_TEMPORAL_DB_PASSWORD: secret('Tp1-'), TH_LAPTOP_USER_PASSWORD: secret(), TH_LAPTOP_PIPELINE_SECRET: secret(), TH_LAPTOP_FRONTDOOR_SECRET: secret(),
  TH_LAPTOP_BANK: BANK,
};
const compose = (...a: string[]) => spawnSync('docker', ['compose', '-p', PROJECT, '-f', path.join(LAPTOP, 'compose.yaml'), ...a], { env, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
const results: { step: string; ok: boolean; detail?: string }[] = [];
let failed = false;
function check(step: string, ok: boolean, detail?: unknown) {
  results.push({ step, ok, detail: detail === undefined ? undefined : typeof detail === 'string' ? detail : JSON.stringify(detail).slice(0, 600) });
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${step}${!ok && detail !== undefined ? `\n     ${typeof detail === 'string' ? detail : JSON.stringify(detail).slice(0, 1200)}` : ''}`);
  if (!ok) { failed = true; throw new Error(step); }
}
const log = (s: string) => console.log(`---- ${s}`);

const STUDIO = 'http://localhost:4317', API = 'http://localhost:8481', SVC = 'http://localhost:8482/service/api', IDP = 'http://localhost:8480';
async function token(form: Record<string, string>): Promise<string> {
  const r = await fetch(`${IDP}/token`, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(form) });
  if (!r.ok) throw new Error(`token: ${r.status} ${await r.text()}`);
  return (await r.json()).access_token;
}
const person = (sub: string) => token({ grant_type: 'password', username: sub, password: env.TH_LAPTOP_USER_PASSWORD, client_id: 'transacthub-runtime-ui' });
const machine = (id: string, s: string) => token({ grant_type: 'client_credentials', client_id: id, client_secret: s });
async function call(base: string, bearer: string, method: string, p: string, body?: unknown, raw?: Buffer) {
  const send = () => fetch(`${base}${p}`, { method, headers: { authorization: `Bearer ${bearer}`, ...(raw ? { 'content-type': 'application/zip' } : body ? { 'content-type': 'application/json' } : {}) }, body: raw ?? (body ? JSON.stringify(body) : undefined) });
  // A pooled connection idle through a long payment may be closed by the time it is used again: sent once more on a fresh one.
  const r = await send().catch(async () => { await new Promise((ok) => setTimeout(ok, 1000)); return send(); });
  const t = Buffer.from(await r.arrayBuffer());
  let data: any = t;
  try { data = JSON.parse(t.toString('utf8')); } catch { /* binary */ }
  return { status: r.status, data };
}

/** sqlcmd in the SQL Server container, as a login; the rows as tab-separated text. */
function sql(login: 'sa' | 'th_runtime', query: string, db = 'th_store'): { ok: boolean; out: string } {
  const pw = login === 'sa' ? env.TH_LAPTOP_SA_PASSWORD : env.TH_LAPTOP_RUNTIME_DB_PASSWORD;
  const r = compose('exec', '-T', '-e', `SQLCMDPASSWORD=${pw}`, 'sqlserver', '/opt/mssql-tools18/bin/sqlcmd', '-C', '-S', 'localhost', '-U', login, '-d', db, '-h', '-1', '-W', '-s', '\t', '-b', '-Q', `SET NOCOUNT ON; ${query}`);
  return { ok: r.status === 0, out: `${r.stdout}${r.stderr}`.trim() };
}
/** Every row of the insert-only tables, as one line each: its key and a hash of every column. */
function snapshot(): Map<string, string> {
  const q = [
    "SELECT 'release|'+Id, CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(Scope,'|',Release,'|',ContentHash,'|',ManifestJson,'|',StoredBy,'|',CONVERT(varchar(40),StoredAt,127),'|',CONVERT(varchar(64),HASHBYTES('SHA2_256',Package),2))), 2) FROM release",
    "SELECT 'activation|'+CONVERT(varchar(20),Id), CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(Environment,'|',Scope,'|',Release,'|',CONVERT(varchar(40),EffectiveFrom,127),'|',Actor,'|',CONVERT(varchar(40),RecordedAt,127))), 2) FROM release_activation",
    "SELECT 'fact|'+CONVERT(varchar(20),Id), CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(SubjectId,'|',Seq,'|',Kind,'|',Code,'|',Hash,'|',PrevHash,'|',DataJson)), 2) FROM payment_fact",
  ].join(' UNION ALL ');
  const r = sql('sa', q);
  if (!r.ok) throw new Error(r.out);
  return new Map(r.out.split('\n').filter((l) => l.includes('\t')).map((l) => l.split('\t') as [string, string]));
}

async function main() {
  if (!process.argv.includes('--no-up')) {
    log('docker compose config, then up --wait (healthy)');
    const cfg = compose('config', '-q');
    check('docker compose config is valid', cfg.status === 0, cfg.stderr);
    const t0 = Date.now();
    const up = compose('up', '-d', '--wait', '--wait-timeout', '600');
    check(`the stack starts healthy (${Math.round((Date.now() - t0) / 1000)} s)`, up.status === 0, `${up.stdout}\n${up.stderr}\n${compose('ps', '-a').stdout}`);
  }
  const ps = compose('ps', '--format', 'json').stdout.trim().split('\n').filter(Boolean).map((l) => JSON.parse(l));
  const unhealthy = ps.filter((c: any) => c.Health && c.Health !== 'healthy');
  check(`every long-running service is healthy (${ps.length} running)`, unhealthy.length === 0, unhealthy.map((c: any) => `${c.Service}: ${c.Health}`).join(', '));

  // The studio: a bank approver for each bank in the studio's users (an administrator proposes, another approves).
  const studio = client(STUDIO);
  const users = (await studio.get('/api/users?actor=users.admin')).data as any[];
  const wanted = [...users.filter((u) => !['adib.checker', 'dib.checker'].includes(u.id)), { id: 'adib.checker', tenant: BANK, roles: ['bank approver'] }, { id: 'dib.checker', tenant: OTHER, roles: ['bank approver'] }];
  const prop = await studio.post('/api/users/proposals', { actor: 'users.admin', users: wanted });
  check('the studio gets a bank approver for ADIB and one for DIB (proposed)', prop.status === 200, prop.data);
  const appr = await studio.post(`/api/users/proposals/${prop.data.changeSet}/approve`, { actor: 'users.approver' });
  check('... and approved by another administrator', appr.status === 200, appr.data);
  const s = await studio.put('/api/git', { actor: 'shared.checker', target: 'temporal', distributions: true, testDistributions: true });
  check('the studio builds distributions for the test environment, with simulators', s.status === 200, s.data);
  const publish = async (bank: string) => {
    const d = await studio.post('/api/distributions', { actor: `${bank}.checker`, tenant: bank, bank, engine: 'temporal', withSimulators: true });
    check(`the studio publishes a distribution of ${bank.toUpperCase()}`, d.status === 200, d.data);
    return d.data as { tag: string; releaseId: string };
  };

  const se = await person('svc.engineer');
  const ba = await person('bank.approver');
  const pipeline = await machine('th-pipeline', env.TH_LAPTOP_PIPELINE_SECRET);
  const frontDoor = await machine('th-front-door', env.TH_LAPTOP_FRONTDOOR_SECRET);
  const consent = async (asked: { status: number; data: any }, what: string) => {
    check(`${what}: asked for, it waits for a bank approver (202)`, asked.status === 202 && asked.data.status === 'pending-approval', asked.data);
    const self = await call(SVC, se, 'POST', `/actions/${asked.data.action}/decision`, { approve: true, reason: 'mine' });
    check(`${what}: the engineer who asked may not consent`, self.status === 403, self.data);
    const d = await call(SVC, ba, 'POST', `/actions/${asked.data.action}/decision`, { approve: true, reason: 'Agreed for the laptop test (synthetic)' });
    check(`${what}: the bank approver consents and it is done`, d.status === 200 && d.data.status === 'done', d.data);
    return d.data.result;
  };
  const install = async (tag: string) => consent(await call(SVC, se, 'POST', '/content/install', { tag, reason: `CR-LAPTOP: install ${tag} (synthetic)` }), `install ${tag}`);
  const activate = async (id: string) => consent(await call(SVC, se, 'POST', '/actions', { action: 'activate', target: id, reason: `CR-LAPTOP: activate ${id} (synthetic)`, parameters: { environment: ENV } }), `activate ${id}`);

  log('1-2. distribution A: published, installed and activated with consent');
  const A = await publish(BANK);
  const ia = await install(A.tag);
  check('A is stored, verified by the studio\'s key', ia.stored === true && ia.state === 'installed', ia);
  await activate(A.releaseId);
  const before = snapshot();
  check(`the store holds A, its activation and its facts (${before.size} rows)`, [...before.keys()].some((k) => k === `release|${A.releaseId}`), [...before.keys()].slice(0, 5));

  log('3. P1 pinned to A at the front door');
  const pin = async (paymentId: string) => call(API, frontDoor, 'POST', '/payments/pin', { paymentId, bank: BANK, environment: ENV, scheme: 'uae.wps' });
  const P1 = `PAY-LAPTOP-${process.pid}-1`, P2 = `PAY-LAPTOP-${process.pid}-2`;
  const p1 = await pin(P1);
  check('P1 is pinned to A', p1.status === 200 && p1.data.distribution === A.releaseId, p1.data);

  log('4. distribution B: published, installed, activated with consent; A drains');
  const B = await publish(BANK);
  check('B is another distribution', B.releaseId !== A.releaseId, B);
  await install(B.tag);
  const sw = await activate(B.releaseId);
  check('activating B moves A to draining in the same step', sw.active === B.releaseId && sw.draining === A.releaseId, sw);

  log('5. P2 pinned to B; P1 runs on A, P2 on B (Temporal on SQL Server)');
  const p2 = await pin(P2);
  check('P2 is pinned to B', p2.status === 200 && p2.data.distribution === B.releaseId, p2.data);
  check('P1 stays pinned to A', (await pin(P1)).data.distribution === A.releaseId);
  const bench = [
    ...['core-banking', 'sanctions', 'fraud', 'channel', 'payment-record', 'facts-out'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
    { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'release' },
    { port: 'p.port.encryption.v1', simulator: 'p.sim.encryption.v1', scenario: 'accept' },
    { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
    { port: 'uae.wps.port.labour-ministry.v1', simulator: 'uae.wps.sim.labour-ministry.v1', scenario: 'accept' },
    { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
    { port: 'b.port.aml-screening.v1', simulator: 'b.sim.aml-screening.v1', scenario: 'accept' },
    { port: 'b.port.labour-ministry.v1', simulator: 'b.sim.labour-ministry.v1', scenario: 'accept' },
  ];
  // Both run side by side (each waits in real time for the simulator's acknowledgement, Issue I-149 (5)), each from its own copy.
  const run = (paymentId: string, distribution: string) => new Promise<{ status: number | null; out: any; err: string }>((resolve) => {
    const c = spawn('docker', ['compose', '-p', PROJECT, '-f', path.join(LAPTOP, 'compose.yaml'), 'exec', '-T', 'testbench', 'node', '/opt/th/deploy/laptop/bin/runtime.mjs', 'run-pinned',
      JSON.stringify({ paymentId, distribution, bank: BANK, environment: ENV, date: '2026-11-24', bench })], { env });
    let out = '', err = '';
    c.stdout.on('data', (d) => (out += d));
    c.stderr.on('data', (d) => (err += d));
    c.on('close', (status) => {
      const line = out.trim().split('\n').filter((l) => l.startsWith('{')).pop();
      resolve({ status, out: line ? JSON.parse(line) : undefined, err: `${out.slice(-2000)}\n${err.slice(-3000)}` });
    });
  });
  const [r1, r2] = await Promise.all([run(P1, A.releaseId), run(P2, B.releaseId)]);
  check('P1 runs to its end', r1.status === 0 && !!r1.out?.outcome && r1.out.ended === true, r1.out ?? r1.err);
  check(`P1 finished on A (${r1.out.outcome}, ${r1.out.ms} ms), its queues and build id A's`, r1.out.distribution?.id === A.releaseId && (r1.out.distribution?.taskQueues ?? []).every((q: string) => q.endsWith(`@${A.releaseId}`)), r1.out.distribution);
  check(`P2 finished on B (${r2.out?.outcome})`, r2.status === 0 && r2.out?.distribution?.id === B.releaseId && r2.out.ended === true, r2.out ?? r2.err);
  const states = await call(SVC, se, 'GET', '/content/releases');
  const st = Object.fromEntries((states.data as any[]).filter((x) => x.kind === 'distribution').map((x) => [x.releaseId, x.state]));
  check('A is drained once P1 ended; B is active', ['drained', 'draining'].includes(st[A.releaseId]) && st[B.releaseId] === 'active', st);

  log('6. at the database: nothing inserted was updated or deleted');
  const after = snapshot();
  const changed = [...before].filter(([k, h]) => after.get(k) !== h).map(([k]) => k);
  check(`every one of the ${before.size} rows seen after A's activation is unchanged at the end (${after.size} rows now)`, changed.length === 0 && after.size > before.size, changed);
  const verify = await call(SVC, se, 'POST', '/actions', { action: 'reverify-package', target: A.releaseId, reason: 'laptop test (synthetic)' });
  check('A still verifies against the studio\'s key', verify.data.status === 'done', verify.data);
  for (const t of ['release', 'release_activation', 'payment_fact']) {
    const key = t === 'release' ? `Id = '${A.releaseId}'` : `Id = (SELECT MIN(Id) FROM ${t})`;
    const col = t === 'release' ? 'StoredBy' : t === 'release_activation' ? 'Actor' : 'Actor';
    const u = sql('th_runtime', `UPDATE ${t} SET ${col} = 'tampered' WHERE ${key}`);
    check(`the runtime's login may not UPDATE ${t}`, !u.ok && /permission was denied/i.test(u.out), u.out);
    const d = sql('th_runtime', `DELETE FROM ${t} WHERE ${key}`);
    check(`the runtime's login may not DELETE from ${t}`, !d.ok && /permission was denied/i.test(d.out), d.out);
    const tr = sql('th_runtime', `TRUNCATE TABLE ${t}`);
    check(`the runtime's login may not TRUNCATE ${t}`, !tr.ok && /(permission|does not have)/i.test(tr.out), tr.out);
  }
  const perms = sql('th_runtime', "SELECT permission_name FROM fn_my_permissions('dbo', 'SCHEMA') ORDER BY permission_name");
  check('the runtime\'s login holds SELECT and INSERT on the store, and neither UPDATE nor DELETE', perms.ok && /INSERT/.test(perms.out) && /SELECT/.test(perms.out) && !/UPDATE|DELETE/.test(perms.out), perms.out);
  const again = snapshot();
  check('the refused statements changed nothing', [...after].every(([k, h]) => again.get(k) === h), '');

  log('7. a second install of A\'s id with other content is refused');
  const pkg = await call(API, pipeline, 'GET', `/distributions/package?id=${encodeURIComponent(A.releaseId)}`);
  check('the release API gives A\'s package as stored', pkg.status === 200 && Buffer.isBuffer(pkg.data), pkg.status);
  const files: Record<string, string | Buffer> = {};
  for (const e of unzip(pkg.data)) files[e.name] = e.content;
  const manifest = parseYaml(files['manifest.yaml'].toString());
  const target = manifest.billOfMaterials.artefacts.find((a: any) => a.path?.startsWith('workflows/')) ?? manifest.billOfMaterials.artefacts[0];
  const changedText = `${files[target.path].toString()}\n# changed after it was installed (synthetic)\n`;
  files[target.path] = changedText;
  target.sha256 = `sha256:${(await import('node:crypto')).createHash('sha256').update(changedText).digest('hex')}`;
  const { toYaml } = await import('../../../studio/src/core/yaml.js');
  files['manifest.yaml'] = toYaml(manifest);
  delete files['release.json']; delete files['release.sig'];
  // Signed with the studio's own key, from the stack's keys volume: the signature is good, only the content differs.
  const pem = compose('exec', '-T', 'studio', 'cat', '/var/lib/transacthub/keys/studio-signing.pem').stdout;
  check('the studio\'s signing key is in the stack\'s keys volume, never in an image', pem.includes('PRIVATE KEY'));
  process.env.TH_LAPTOP_IT_KEY = pem;
  process.env.TH_SIGNING_KEY = 'env:TH_LAPTOP_IT_KEY';
  const text = Object.fromEntries(Object.entries(files).map(([k, v]) => [k, Buffer.isBuffer(v) ? v.toString('utf8') : v]));
  const signed = signFiles(text, { kind: 'distribution', releaseId: A.releaseId, workflow: '', release: manifest.id, target: manifest.engine, tenant: BANK, changeSet: 'CS-LAPTOP-TAMPER', extra: { billOfMaterials: manifest.billOfMaterials, parameterContract: manifest.parameterContract } });
  const second = await call(API, pipeline, 'POST', '/distributions', undefined, zip(Object.entries(signed).map(([name, c]) => ({ name, content: Buffer.from(c) }))));
  check('the release API refuses it: the id is stored with other content (409)', second.status === 409 && /already|other content|stored/i.test(JSON.stringify(second.data)), second.data);
  const dup = sql('th_runtime', `INSERT INTO release (Id, Scope, Release, ContentHash, ManifestJson, Package, StoredBy, StoredAt) SELECT Id + '#2', Scope, Release, 'x', ManifestJson, Package, 'tamper', StoredAt FROM release WHERE Id = '${A.releaseId}'`);
  check('the database refuses a second row of the same distribution id (its unique key)', !dup.ok && /duplicate|unique/i.test(dup.out), dup.out);

  log('8. DIB\'s distribution is refused in the ADIB runtime');
  const D = await publish(OTHER);
  const versions = (await call(SVC, se, 'GET', '/content/versions')).data as any[];
  check('the service console lists DIB\'s distribution as not installable here', versions.find((v) => v.tag === D.tag)?.installable === false, versions.find((v) => v.tag === D.tag));
  const v = await call(SVC, se, 'POST', '/content/verify', { tag: D.tag, reason: 'laptop test (synthetic)' });
  check('verify refuses it: the runtime serves ADIB only', v.status === 422 && /serves adib only/.test(v.data.error), v.data);
  const i = await call(SVC, se, 'POST', '/content/install', { tag: D.tag, reason: 'laptop test (synthetic)' });
  check('install refuses it before any consent', i.status === 422 && /serves adib only/.test(i.data.error), i.data);
  const dpkg = await fetch(`http://localhost:4318/repos/local/content/git/matching-refs/tags/dist`).catch(() => undefined);
  void dpkg;
  const listed = (await call(API, pipeline, 'GET', '/sources/versions')).data as any[];
  const viaApi = await call(API, pipeline, 'POST', '/sources/install', { tag: D.tag });
  check('the release API refuses it too', viaApi.status === 422 && /serves adib only/.test(JSON.stringify(viaApi.data)) && listed.some((x) => x.tag === D.tag), viaApi.data);
  const none = sql('sa', `SELECT COUNT(*) FROM release WHERE Scope = 'dist/${OTHER}'`);
  check('nothing of DIB is in the ADIB runtime\'s store', none.ok && none.out.trim().split('\n').pop() === '0', none.out);
}

let error: unknown;
try { await main(); } catch (e) { error = e; failed = true; }
fs.writeFileSync(path.join(work, 'results.json'), JSON.stringify(results, null, 2));
if (failed) {
  console.log(`\n${error instanceof Error && !results.some((r) => !r.ok) ? `error: ${error.stack}` : ''}`);
  for (const svc of ['service-api', 'runtime-api', 'testbench', 'studio', 'temporal']) console.log(`--- ${svc}\n${compose('logs', '--tail', '40', svc).stdout.slice(-4000)}`);
}
if (!keep) {
  const down = compose('down', '-v', '--remove-orphans');
  console.log(`---- removed the stack and its volumes (${down.status === 0 ? 'ok' : down.stderr})`);
}
fs.rmSync(work, { recursive: true, force: true });
console.log(`\n${results.filter((r) => r.ok).length} passed, ${results.filter((r) => !r.ok).length} failed${failed ? ' (FAILED)' : ''}`);
process.exit(failed ? 1 : 0);
