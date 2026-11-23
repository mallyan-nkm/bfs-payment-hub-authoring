// "Insert, never update" for workflows, proved in the laptop deployment (deploy/laptop, spec R-09-80, R-09-81, R-09-76, R-09-77),
// with every payment's file entering as a bank's would (R-09-105).
//
//   cd studio && node_modules/.bin/tsx ../deploy/laptop/test/insert-only.mts [--keep] [--no-up] [--postgres]
//
// With --postgres (or TH_LAPTOP_IT_DB=postgres) the stack runs with compose.postgres.yaml: the store, the read model and Temporal on
// PostgreSQL 16, its logins from sql/grants-postgres.sql (R-09-140), and every check at the database is made there.
//
// It brings the compose stack up healthy (the images built by deploy/laptop/build.sh, TH_LAPTOP_TAG), with passwords, client
// secrets and the SFTP server's login made for this run and given to compose by its environment only, the SFTP server's host keys
// read out of band and pinned by reference, and then, end to end over the services' own APIs:
//  1. publishes distribution A of the runtime's bank (ADIB) from the studio;
//  2. installs and activates A through the service console's API: a service engineer asks, a bank approver consents;
//  3. drops salary file 1 on the SFTP server's pick-up folder; the File API's inbox poller takes it, checks and keeps it, and its
//     front door pins it to A, runs the intake map on A's workers, routes it and starts payment P1 on the engine host (Temporal);
//  4. publishes B, installs it and activates it with consent: A drains, while P1 runs on;
//  5. refuses a second install of A's id with other content (signed by the studio's own key), at the release API and at the
//     database's unique key;
//  6. refuses a distribution of DIB, published by the same studio and signed by the same key, in the ADIB runtime: at verify
//     and at install in the service console, and at the release API;
//  7. drops salary file 2 on the SFTP server (P2) and posts salary file 3 to the File API's POST /files as the channel (P3): both
//     are pinned to B; P1 finishes on A, P2 and P3 on B; the taken files are moved out of the inbox; a file sent goes out through
//     the File API's outbox onto the SFTP server, and the consoles' resend through it sends the same bytes again;
//  8. checks at the database that no row of a distribution, its definitions, its activations or the fact log was updated or
//     deleted: every row seen after step 2 is unchanged at the end, and the runtime's login is refused an UPDATE, a DELETE and
//     a TRUNCATE on each of those tables by the database itself.
// The host ports are the stack's defaults unless TH_LAPTOP_IT_PORTS=<base> moves them (base, base+1, ...), so that the test can
// run beside another laptop stack. It removes its containers and volumes at the end unless --keep is given.
import { spawnSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
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
/** The database the stack runs on: SQL Server by default, PostgreSQL with --postgres (R-09-140). */
const PG = process.argv.includes('--postgres') || process.env.TH_LAPTOP_IT_DB === 'postgres';
const DB_NAME = PG ? 'PostgreSQL' : 'SQL Server';
const FILES_ARGS = ['-f', path.join(LAPTOP, 'compose.yaml'), ...(PG ? ['-f', path.join(LAPTOP, 'compose.postgres.yaml')] : [])];
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-laptop-it-'));
const secret = (prefix = '') => prefix + randomBytes(15).toString('base64url').replace(/[^A-Za-z0-9]/g, 'x');
const base = Number(process.env.TH_LAPTOP_IT_PORTS ?? 0);
const port = (n: number, d: number) => String(base ? base + n : d);
const PORTS = {
  TH_LAPTOP_STUDIO_PORT: port(0, 4317), TH_LAPTOP_IDP_PORT: port(1, 8480), TH_LAPTOP_API_PORT: port(2, 8481), TH_LAPTOP_SERVICE_PORT: port(3, 8482),
  TH_LAPTOP_OPS_PORT: port(4, 8483), TH_LAPTOP_TESTBENCH_PORT: port(5, 8484), TH_LAPTOP_FILE_API_PORT: port(6, 8485), TH_LAPTOP_SQL_PORT: port(7, 14330),
  TH_LAPTOP_TEMPORAL_PORT: port(8, 17233), TH_LAPTOP_SFTP_PORT: port(9, 12222),
};
const env: Record<string, string> = {
  ...process.env as Record<string, string>,
  TH_LAPTOP_SA_PASSWORD: secret('Sa1-'), TH_LAPTOP_RUNTIME_DB_PASSWORD: secret('Rt1-'), TH_LAPTOP_TESTBENCH_DB_PASSWORD: secret('Tb1-'),
  TH_LAPTOP_TEMPORAL_DB_PASSWORD: secret('Tp1-'), TH_LAPTOP_USER_PASSWORD: secret(), TH_LAPTOP_PIPELINE_SECRET: secret(), TH_LAPTOP_FRONTDOOR_SECRET: secret(), TH_LAPTOP_ENGINE_HOST_TOKEN: secret(),
  TH_LAPTOP_CHANNEL_SECRET: secret(), TH_LAPTOP_OPERATIONS_SECRET: secret(), TH_LAPTOP_SFTP_PASSWORD: secret('Sf1-'),
  TH_LAPTOP_BANK: BANK, ...PORTS,
  TH_LAPTOP_IDP_URL: `http://localhost:${PORTS.TH_LAPTOP_IDP_PORT}`, TH_LAPTOP_STUDIO_URL: `http://localhost:${PORTS.TH_LAPTOP_STUDIO_PORT}`,
};
const compose = (...a: string[]) => spawnSync('docker', ['compose', '-p', PROJECT, ...FILES_ARGS, ...a], { env, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
const results: { step: string; ok: boolean; detail?: string }[] = [];
let failed = false;
function check(step: string, ok: boolean, detail?: unknown) {
  results.push({ step, ok, detail: detail === undefined ? undefined : typeof detail === 'string' ? detail : JSON.stringify(detail).slice(0, 600) });
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${step}${!ok && detail !== undefined ? `\n     ${typeof detail === 'string' ? detail : JSON.stringify(detail).slice(0, 1200)}` : ''}`);
  if (!ok) { failed = true; throw new Error(step); }
}
const log = (s: string) => console.log(`---- ${s}`);
const sleep = (ms: number) => new Promise((ok) => setTimeout(ok, ms));

const STUDIO = `http://localhost:${PORTS.TH_LAPTOP_STUDIO_PORT}`, API = `http://localhost:${PORTS.TH_LAPTOP_API_PORT}`;
const SVC = `http://localhost:${PORTS.TH_LAPTOP_SERVICE_PORT}/service/api`, IDP = `http://localhost:${PORTS.TH_LAPTOP_IDP_PORT}`;
const FILES = `http://localhost:${PORTS.TH_LAPTOP_FILE_API_PORT}`;
async function token(form: Record<string, string>): Promise<string> {
  const r = await fetch(`${IDP}/token`, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(form) });
  if (!r.ok) throw new Error(`token: ${r.status} ${await r.text()}`);
  return (await r.json()).access_token;
}
const person = (sub: string) => token({ grant_type: 'password', username: sub, password: env.TH_LAPTOP_USER_PASSWORD, client_id: 'transacthub-runtime-ui' });
const machine = (id: string, s: string) => token({ grant_type: 'client_credentials', client_id: id, client_secret: s });
async function call(baseUrl: string, bearer: string, method: string, p: string, body?: unknown, raw?: Buffer) {
  const send = () => fetch(`${baseUrl}${p}`, { method, headers: { authorization: `Bearer ${bearer}`, ...(raw ? { 'content-type': 'application/zip' } : body ? { 'content-type': 'application/json' } : {}) }, body: raw ?? (body ? JSON.stringify(body) : undefined) });
  // A pooled connection idle through a long payment may be closed by the time it is used again: sent once more on a fresh one.
  const r = await send().catch(async () => { await sleep(1000); return send(); });
  const t = Buffer.from(await r.arrayBuffer());
  let data: any = t;
  try { data = JSON.parse(t.toString('utf8')); } catch { /* binary */ }
  return { status: r.status, data };
}

/** sqlcmd in the SQL Server container, or psql in the PostgreSQL one, as a login ('sa' is the server's owner); the rows as tab-separated text. */
function sql(login: 'sa' | 'th_runtime', query: string, db = 'th_store'): { ok: boolean; out: string } {
  const pw = login === 'sa' ? env.TH_LAPTOP_SA_PASSWORD : env.TH_LAPTOP_RUNTIME_DB_PASSWORD;
  const r = PG
    ? compose('exec', '-T', '-e', `PGPASSWORD=${pw}`, 'postgres', 'psql', '-h', '127.0.0.1', '-U', login === 'sa' ? 'postgres' : login, '-d', db, '-At', '-F', '\t', '-v', 'ON_ERROR_STOP=1', '-c', query)
    : compose('exec', '-T', '-e', `SQLCMDPASSWORD=${pw}`, 'sqlserver', '/opt/mssql-tools18/bin/sqlcmd', '-C', '-S', 'localhost', '-U', login, '-d', db, '-h', '-1', '-W', '-s', '\t', '-b', '-Q', `SET NOCOUNT ON; ${query}`);
  return { ok: r.status === 0, out: `${r.stdout}${r.stderr}`.trim() };
}
/** A column of the store as each database names it: PostgreSQL keeps the model's names quoted. */
const c = (name: string) => (PG ? `"${name}"` : name);
/** The refusal each database gives a statement the login may not make. */
const DENIED = PG ? /permission denied/i : /permission was denied/i;
const lastLine = (s: string) => s.trim().split('\n').pop()?.trim() ?? '';
/** Every row of the insert-only tables, as one line each: its key and a hash of every column. */
function snapshot(): Map<string, string> {
  const q = PG ? [
    `SELECT 'release|'||"Id", encode(sha256(convert_to(concat_ws('|',"Scope","Release","ContentHash","ManifestJson","StoredBy","StoredAt"::text,encode(sha256("Package"),'hex')),'UTF8')),'hex') FROM release`,
    `SELECT 'activation|'||"Id", encode(sha256(convert_to(concat_ws('|',"Environment","Scope","Release","EffectiveFrom"::text,"Actor","RecordedAt"::text,"Sequence"::text),'UTF8')),'hex') FROM release_activation`,
    `SELECT 'fact|'||"Id", encode(sha256(convert_to(concat_ws('|',"SubjectId","Seq"::text,"Kind","Code","Hash","PrevHash","DataJson"),'UTF8')),'hex') FROM payment_fact`,
  ].join(' UNION ALL ') : [
    "SELECT 'release|'+Id, CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(Scope,'|',Release,'|',ContentHash,'|',ManifestJson,'|',StoredBy,'|',CONVERT(varchar(40),StoredAt,127),'|',CONVERT(varchar(64),HASHBYTES('SHA2_256',Package),2))), 2) FROM release",
    "SELECT 'activation|'+CONVERT(varchar(20),Id), CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(Environment,'|',Scope,'|',Release,'|',CONVERT(varchar(40),EffectiveFrom,127),'|',Actor,'|',CONVERT(varchar(40),RecordedAt,127),'|',Sequence)), 2) FROM release_activation",
    "SELECT 'fact|'+CONVERT(varchar(20),Id), CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(SubjectId,'|',Seq,'|',Kind,'|',Code,'|',Hash,'|',PrevHash,'|',DataJson)), 2) FROM payment_fact",
  ].join(' UNION ALL ');
  const r = sql('sa', q);
  if (!r.ok) throw new Error(r.out);
  return new Map(r.out.split('\n').filter((l) => l.includes('\t')).map((l) => l.split('\t') as [string, string]));
}

// The SFTP server as the bank's partners see it: the channel's pick-up folder, and where the bank's files go out.
const SFTP_HOME = '/home/th/upload';
const PICKUP = `${SFTP_HOME}/wps/inbound`;
/** The channel drops a file on the SFTP server: written beside the folder, then moved in whole, as an SFTP client renames its upload. */
function drop(name: string, content: string) {
  const r = spawnSync('docker', ['compose', '-p', PROJECT, ...FILES_ARGS, 'exec', '-T', '-u', '1001', 'sftp', 'sh', '-c',
    `mkdir -p ${PICKUP} ${SFTP_HOME}/wps/uploading && cat > ${SFTP_HOME}/wps/uploading/${name} && mv ${SFTP_HOME}/wps/uploading/${name} ${PICKUP}/${name}`], { env, input: content, encoding: 'utf8' });
  if (r.status !== 0) throw new Error(`drop ${name}: ${r.stderr}`);
}
const ls = (dir: string) => compose('exec', '-T', 'sftp', 'sh', '-c', `ls -1 ${dir} 2>/dev/null || true`).stdout.split('\n').map((l) => l.trim()).filter(Boolean);
const cat = (file: string) => compose('exec', '-T', 'sftp', 'cat', file).stdout;

/**
 * A synthetic salary file of ADIB's employer, its own by its creation time and reference (the File API refuses a duplicate). It
 * keeps the samples' `# file:` first line: the intake map reads the file's name from it, not from the File API (Issue I-195).
 */
function salaryFile(n: number): { name: string; text: string } {
  const sample = fs.readFileSync(path.resolve(LAPTOP, '../../studio/fixtures/samples/wps-sif-valid.sif'), 'utf8');
  const time = `083${n}`;
  const name = `0000001234567261117${time}00.sif`;
  const text = sample.split('\n').map((l) => (l.startsWith('# file:') ? `# file: ${name}` : l)).join('\n').replace(',0830,', `,${time},`).replace('REF-0001', `REF-000${n}`);
  return { name, text };
}

/** Waits until the File API has kept a file of this name and its front door started a payment from it: the payment's id. */
async function startedFrom(fileName: string, ms = 180000): Promise<{ paymentId: string; fileId: string }> {
  const until = Date.now() + ms;
  for (;;) {
    const r = PG
      ? sql('sa', `SELECT f."SubjectId" || '|' || f."Id" FROM stored_file f WHERE f."FileName" = '${fileName}' AND f."Direction" = 'in' AND EXISTS (SELECT 1 FROM payment_fact p WHERE p."SubjectId" = f."SubjectId" AND p."Kind" = 'frontdoor.started') LIMIT 1`)
      : sql('sa', `SELECT TOP 1 f.SubjectId + '|' + f.Id FROM stored_file f WHERE f.FileName = '${fileName}' AND f.Direction = 'in' AND EXISTS (SELECT 1 FROM payment_fact p WHERE p.SubjectId = f.SubjectId AND p.Kind = 'frontdoor.started')`);
    const line = lastLine(r.out);
    if (r.ok && line.includes('|')) {
      const [paymentId, fileId] = line.split('|');
      return { paymentId, fileId };
    }
    if (Date.now() > until) throw new Error(`No payment was started from ${fileName}: ${r.out}\n${compose('logs', '--tail', '60', 'file-api').stdout.slice(-4000)}`);
    await sleep(2000);
  }
}

async function main() {
  if (!process.argv.includes('--no-up')) {
    log('docker compose config; the SFTP server first, its host keys read out of band and pinned; then up --wait (healthy)');
    const cfg = compose('config', '-q');
    check('docker compose config is valid', cfg.status === 0, cfg.stderr);
    const s = compose('up', '-d', '--wait', '--wait-timeout', '180', 'sftp');
    check('the SFTP server starts with a login made for this run', s.status === 0, `${s.stdout}\n${s.stderr}`);
    const prints = ['ed25519', 'rsa', 'ecdsa'].map((k) => compose('exec', '-T', 'sftp', 'ssh-keygen', '-lf', `/etc/ssh/ssh_host_${k}_key.pub`)).filter((r) => r.status === 0).map((r) => r.stdout.split(' ')[1]);
    check(`its host keys are read out of band (${prints.length}) and pinned by reference in the File API's binding`, prints.length > 0 && prints.every((x) => /^SHA256:/.test(x)), prints);
    env.TH_LAPTOP_SFTP_HOST_KEY = prints.join(',');
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
  const set = await studio.put('/api/git', { actor: 'shared.checker', target: 'temporal', distributions: true, testDistributions: true });
  check('the studio builds distributions for the test environment, with simulators', set.status === 200, set.data);
  const publish = async (bank: string) => {
    const d = await studio.post('/api/distributions', { actor: `${bank}.checker`, tenant: bank, bank, engine: 'temporal', withSimulators: true });
    check(`the studio publishes a distribution of ${bank.toUpperCase()}`, d.status === 200, d.data);
    return d.data as { tag: string; releaseId: string };
  };

  const se = await person('svc.engineer');
  const ba = await person('bank.approver');
  const pipeline = await machine('th-pipeline', env.TH_LAPTOP_PIPELINE_SECRET);
  const frontDoor = await machine('th-front-door', env.TH_LAPTOP_FRONTDOOR_SECRET);
  const channel = await machine('th-channel', env.TH_LAPTOP_CHANNEL_SECRET);
  const operations = await machine('th-operations', env.TH_LAPTOP_OPERATIONS_SECRET);
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
  const pinOf = async (paymentId: string) => (await call(API, pipeline, 'GET', `/payments/pin?paymentId=${encodeURIComponent(paymentId)}`)).data;

  log('1-2. distribution A: published, installed and activated with consent');
  const A = await publish(BANK);
  const ia = await install(A.tag);
  check('A is stored, verified by the studio\'s key', ia.stored === true && ia.state === 'installed', ia);
  // The bank's values (its rail, its WPS values, its cut-offs if it has any) reach the workers from its installed configuration
  // release, checked against A's parameter contract (R-12-28), and with them the laptop's binding of its file transfer, the stack's
  // SFTP server (R-09-105, R-09-110 to R-09-112): the studio builds the release, the runtime puts it in force with consent.
  const laptopBindings = parseYaml(fs.readFileSync(path.join(LAPTOP, 'banks', BANK, 'file-transfer.yaml'), 'utf8')).bindings;
  const cr = await studio.post('/api/configuration-releases', { actor: `${BANK}.checker`, tenant: BANK, bank: BANK, bindings: laptopBindings });
  check('the studio publishes ADIB\'s configuration release', cr.status === 200, cr.data);
  const ic = await install(cr.data.tag);
  check(`the configuration release is installed and in force (${ic.values} values, ${ic.bindings} binding)`, ic.stored === true && ic.values > 0 && ic.bindings === laptopBindings.length, ic);
  await activate(A.releaseId);
  const before = snapshot();
  check(`the store holds A, its activation and its facts (${before.size} rows)`, [...before.keys()].some((k) => k === `release|${A.releaseId}`), [...before.keys()].slice(0, 5));

  log('3. salary file 1 dropped on the SFTP server: the File API takes it, and its front door starts P1 on A');
  const f1 = salaryFile(1), f2 = salaryFile(2), f3 = salaryFile(3);
  drop(f1.name, f1.text);
  const p1 = await startedFrom(f1.name);
  const P1 = p1.paymentId;
  check(`the File API's inbox poller took ${f1.name} from the SFTP server and kept it (${p1.fileId})`, !!P1);
  check('the front door pinned P1 to A at acceptance', (await pinOf(P1)).distribution === A.releaseId, await pinOf(P1));
  // The poller moves a file once the intake has answered: the payment may start a moment before the file leaves the inbox.
  for (let i = 0; i < 30 && ls(PICKUP).includes(f1.name); i++) await sleep(1000);
  check('the taken file is moved out of the pick-up folder to its done folder', !ls(PICKUP).includes(f1.name) && ls(`${PICKUP}/done`).includes(f1.name), { inbox: ls(PICKUP), done: ls(`${PICKUP}/done`) });

  log('4. distribution B: published, installed, activated with consent; A drains while P1 runs');
  const B = await publish(BANK);
  check('B is another distribution', B.releaseId !== A.releaseId, B);
  await install(B.tag);
  const sw = await activate(B.releaseId);
  check('activating B moves A to draining in the same step', sw.active === B.releaseId && sw.draining === A.releaseId, sw);

  log('5. a second install of A\'s id with other content is refused');
  const pkg = await call(API, pipeline, 'GET', `/distributions/package?id=${encodeURIComponent(A.releaseId)}`);
  check('the release API gives A\'s package as stored', pkg.status === 200 && Buffer.isBuffer(pkg.data), pkg.status);
  const stored = Object.fromEntries(unzip(pkg.data).map((e) => [e.name, e.content]));
  const original = parseYaml(stored['manifest.yaml'].toString());
  // A's id with other content: one of its workflows changed after it was installed, the rest of the bill of materials left out
  // (a whole distribution is larger than the release API takes in one upload, Issue I-156), signed as the studio signs.
  const target = original.billOfMaterials.artefacts.find((a: any) => a.path?.startsWith('workflows/')) ?? original.billOfMaterials.artefacts[0];
  const changedText = `${stored[target.path].toString()}\n# changed after it was installed (synthetic)\n`;
  const billOfMaterials = { ...original.billOfMaterials, artefacts: [{ ...target, sha256: `sha256:${createHash('sha256').update(changedText).digest('hex')}` }] };
  // The small bundle carries none of A's native assemblies (R-09-120): its manifest names none, so only the id is in question.
  const { native: _native, ...rest } = original;
  const manifest = { ...rest, billOfMaterials };
  const { toYaml } = await import('../../../studio/src/core/yaml.js');
  const files: Record<string, string> = { [target.path]: changedText, 'manifest.yaml': toYaml(manifest) };
  // Signed with the studio's own key, from the stack's keys volume: the signature is good, only the content differs.
  const pem = compose('exec', '-T', 'studio', 'cat', '/var/lib/transacthub/keys/studio-signing.pem').stdout;
  check('the studio\'s signing key is in the stack\'s keys volume, never in an image', pem.includes('PRIVATE KEY'));
  process.env.TH_LAPTOP_IT_KEY = pem;
  process.env.TH_SIGNING_KEY = 'env:TH_LAPTOP_IT_KEY';
  const signed = signFiles(files, { kind: 'distribution', releaseId: A.releaseId, workflow: '', release: manifest.id, target: manifest.engine, tenant: BANK, changeSet: 'CS-LAPTOP-TAMPER', extra: { billOfMaterials: manifest.billOfMaterials, parameterContract: manifest.parameterContract } });
  const second = await call(API, pipeline, 'POST', '/distributions', undefined, zip(Object.entries(signed).map(([name, c]) => ({ name, content: Buffer.from(c) }))));
  check('the release API refuses it: the id is stored with other content (409)', second.status === 409 && /already|other content|stored/i.test(JSON.stringify(second.data)), second.data);
  const cols = ['Id', 'Scope', 'Release', 'ContentHash', 'ManifestJson', 'Package', 'StoredBy', 'StoredAt'].map(c).join(', ');
  const dup = sql('th_runtime', `INSERT INTO release (${cols}) SELECT ${c('Id')} ${PG ? '||' : '+'} '#2', ${c('Scope')}, ${c('Release')}, 'x', ${c('ManifestJson')}, ${c('Package')}, 'tamper', ${c('StoredAt')} FROM release WHERE ${c('Id')} = '${A.releaseId}'`);
  check('the database refuses a second row of the same distribution id (its unique key)', !dup.ok && /duplicate|unique/i.test(dup.out), dup.out);

  log('6. DIB\'s distribution is refused in the ADIB runtime');
  const D = await publish(OTHER);
  const versions = (await call(SVC, se, 'GET', '/content/versions')).data as any[];
  check('the service console lists DIB\'s distribution as not installable here', versions.find((v) => v.tag === D.tag)?.installable === false, versions.find((v) => v.tag === D.tag));
  const v = await call(SVC, se, 'POST', '/content/verify', { tag: D.tag, reason: 'laptop test (synthetic)' });
  check('verify refuses it: the runtime serves ADIB only', v.status === 422 && /serves adib only/.test(v.data.error), v.data);
  const i = await call(SVC, se, 'POST', '/content/install', { tag: D.tag, reason: 'laptop test (synthetic)' });
  check('install refuses it before any consent', i.status === 422 && /serves adib only/.test(i.data.error), i.data);
  const listed = (await call(API, pipeline, 'GET', '/sources/versions')).data as any[];
  const viaApi = await call(API, pipeline, 'POST', '/sources/install', { tag: D.tag });
  check('the release API refuses it too', viaApi.status === 422 && /serves adib only/.test(JSON.stringify(viaApi.data)) && listed.some((x) => x.tag === D.tag), viaApi.data);
  const none = sql('sa', `SELECT COUNT(*) FROM release WHERE ${c('Scope')} = 'dist/${OTHER}'`);
  check('nothing of DIB is in the ADIB runtime\'s store', none.ok && lastLine(none.out) === '0', none.out);

  log(`7. P2 by SFTP and P3 by POST /files, both on B; P1 runs on A, P2 and P3 on B (Temporal on ${DB_NAME})`);
  drop(f2.name, f2.text);
  const up3 = await call(FILES, channel, 'POST', '/files', { tenant: BANK, message: 'uae.wps.sif', fileName: f3.name, contentBase64: Buffer.from(f3.text).toString('base64') });
  check('the channel\'s POST /files is taken and started (202)', up3.status === 202 && up3.data.disposition === 'started' && !!up3.data.payment, up3.data);
  const P3 = up3.data.payment as string;
  check('P3 is pinned to B at acceptance', up3.data.distribution === B.releaseId && (await pinOf(P3)).distribution === B.releaseId, up3.data);
  const P2 = (await startedFrom(f2.name)).paymentId;
  check('P2, from the SFTP server, is pinned to B', (await pinOf(P2)).distribution === B.releaseId, await pinOf(P2));
  check('P1 stays pinned to A', (await pinOf(P1)).distribution === A.releaseId);
  const again1 = await call(FILES, channel, 'POST', '/files', { tenant: BANK, message: 'uae.wps.sif', fileName: `copy-${f3.name}`, contentBase64: Buffer.from(f3.text).toString('base64') });
  check('the same file again is refused as a duplicate (409), and nothing starts', again1.status === 409, again1.data);
  // Each waits in real time for the simulator's acknowledgement (Issue I-156 (5)), on the engine host, each on its distribution's own workers.
  const ends: Record<string, any> = {};
  const until = Date.now() + 40 * 60000;
  while (Object.keys(ends).length < 3 && Date.now() < until) {
    for (const p of [P1, P2, P3]) if (!ends[p]) { const x = await pinOf(p); if (x?.ended) ends[p] = x; }
    if (Object.keys(ends).length < 3) await sleep(15000);
  }
  for (const [p, name] of [[P1, 'P1'], [P2, 'P2'], [P3, 'P3']] as const) check(`${name} runs to its end on the engine host (${ends[p]?.ended ?? 'not ended'}), not failed`, !!ends[p]?.ended && ends[p].ended !== 'failed', `${JSON.stringify(await pinOf(p)).slice(0, 1500)}\n${compose('logs', '--tail', '40', 'enginehost').stdout.slice(-3000)}\n${compose('logs', '--tail', '40', 'workers').stdout.slice(-3000)}`);
  const ranOn = (x: any) => [...new Set((x.facts ?? []).map((f: any) => f.releaseId).filter((r: any) => typeof r === 'string' && r.startsWith('dist/')))];
  check(`P1 finished on A (${ends[P1].ended}): every fact of its run names A`, JSON.stringify(ranOn(ends[P1])) === JSON.stringify([A.releaseId]), ranOn(ends[P1]));
  check(`P2 and P3 finished on B (${ends[P2].ended}, ${ends[P3].ended})`, [P2, P3].every((p) => JSON.stringify(ranOn(ends[p])) === JSON.stringify([B.releaseId])), [ranOn(ends[P2]), ranOn(ends[P3])]);
  const states = await call(SVC, se, 'GET', '/content/releases');
  const st = Object.fromEntries((states.data as any[]).filter((x) => x.kind === 'distribution').map((x) => [x.releaseId, x.state]));
  check('A is drained once P1 ended; B is active', ['drained', 'draining'].includes(st[A.releaseId]) && st[B.releaseId] === 'active', st);
  const kept = await call(FILES, channel, 'GET', `/files?tenant=${BANK}&paymentId=${encodeURIComponent(P1)}`);
  check('P1\'s file is kept once, with its signature, linked to the payment', kept.status === 200 && kept.data.some((f: any) => f.fileName === f1.name && f.direction === 'in' && f.verified === true), kept.data);

  log('7b. outbound: a file sent goes out through the File API\'s outbox onto the SFTP server, and the consoles\' resend sends it again');
  const out = await call(FILES, frontDoor, 'POST', '/outbox', { tenant: BANK, scheme: 'uae.wps', kind: 'uae.wps.sif-submission', folder: 'centralBank/upload/cb/wps/sif', fileName: f1.name.replace(/\.sif$/, '.SIF'), contentBase64: Buffer.from(f1.text).toString('base64'), paymentId: P1, leg: 'scheme-leg', activity: 'laptop-insert-only-test' });
  check(`the outbox keeps the file and delivers it on the bank's binding (${out.data.adapter}, ${out.data.delivery})`, out.status === 201 && out.data.delivery === 'delivered' && out.data.adapter === 'sftp', out.data);
  const sent = `${SFTP_HOME}/cb/wps/sif/${f1.name.replace(/\.sif$/, '.SIF')}`;
  check('the file is on the SFTP server, byte for byte', cat(sent) === f1.text, cat(sent).slice(0, 200));
  compose('exec', '-T', '-u', '1001', 'sftp', 'rm', '-f', sent);
  const resend = await call(FILES, operations, 'POST', `/files/${encodeURIComponent(out.data.file)}/resend`, { actionId: `laptop-${process.pid}`, onBehalfOf: 'ops.operator' });
  check('the consoles\' client resends it through the File API: the same bytes again', resend.status === 200 && resend.data.result === 'resent' && cat(sent) === f1.text, resend.data);
  const once = await call(FILES, operations, 'POST', `/files/${encodeURIComponent(out.data.file)}/resend`, { actionId: `laptop-${process.pid}`, onBehalfOf: 'ops.operator' });
  check('the same action resends once', once.status === 200 && /already/.test(once.data.result), once.data);

  log('8. at the database: nothing inserted was updated or deleted');
  const after = snapshot();
  const changed = [...before].filter(([k, h]) => after.get(k) !== h).map(([k]) => k);
  check(`every one of the ${before.size} rows seen after A's activation is unchanged at the end (${after.size} rows now)`, changed.length === 0 && after.size > before.size, changed);
  const verify = await call(SVC, se, 'POST', '/actions', { action: 'reverify-package', target: A.releaseId, reason: 'laptop test (synthetic)' });
  check('A still verifies against the studio\'s key', verify.data.status === 'done', verify.data);
  for (const t of ['release', 'release_activation', 'payment_fact']) {
    const key = t === 'release' ? `${c('Id')} = '${A.releaseId}'` : `${c('Id')} = (SELECT MIN(${c('Id')}) FROM ${t})`;
    const col = c(t === 'release' ? 'StoredBy' : 'Actor');
    const u = sql('th_runtime', `UPDATE ${t} SET ${col} = 'tampered' WHERE ${key}`);
    check(`the runtime's login may not UPDATE ${t}`, !u.ok && DENIED.test(u.out), u.out);
    const d = sql('th_runtime', `DELETE FROM ${t} WHERE ${key}`);
    check(`the runtime's login may not DELETE from ${t}`, !d.ok && DENIED.test(d.out), d.out);
    const tr = sql('th_runtime', `TRUNCATE TABLE ${t}`);
    check(`the runtime's login may not TRUNCATE ${t}`, !tr.ok && /(permission|does not have)/i.test(tr.out), tr.out);
  }
  const perms = PG
    ? sql('th_runtime', "SELECT DISTINCT privilege_type FROM information_schema.role_table_grants WHERE grantee = 'th_runtime' AND table_catalog = 'th_store' ORDER BY 1")
    : sql('th_runtime', "SELECT permission_name FROM fn_my_permissions('dbo', 'SCHEMA') ORDER BY permission_name");
  check('the runtime\'s login holds SELECT and INSERT on the store, and neither UPDATE nor DELETE', perms.ok && /INSERT/.test(perms.out) && /SELECT/.test(perms.out) && !/UPDATE|DELETE/.test(perms.out), perms.out);
  const again = snapshot();
  check('the refused statements changed nothing', [...after].every(([k, h]) => again.get(k) === h), '');
}

let error: unknown;
try { await main(); } catch (e) { error = e; failed = true; }
fs.writeFileSync(path.join(work, 'results.json'), JSON.stringify(results, null, 2));
if (failed) {
  console.log(`\n${error instanceof Error && !results.some((r) => !r.ok) ? `error: ${error.stack}` : ''}`);
  for (const svc of ['file-api', 'workers', 'enginehost', 'service-api', 'runtime-api', 'studio', 'temporal']) console.log(`--- ${svc}\n${compose('logs', '--tail', '40', svc).stdout.slice(-4000)}`);
}
if (!keep) {
  const down = compose('down', '-v', '--remove-orphans');
  console.log(`---- removed the stack and its volumes (${down.status === 0 ? 'ok' : down.stderr})`);
}
fs.rmSync(work, { recursive: true, force: true });
console.log(`\n${results.filter((r) => r.ok).length} passed, ${results.filter((r) => !r.ok).length} failed${failed ? ' (FAILED)' : ''}`);
process.exit(failed ? 1 : 0);
