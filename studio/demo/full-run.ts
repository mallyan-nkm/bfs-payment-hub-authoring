import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { parseYaml, toYaml } from '../src/core/yaml.js';
import { unzip } from '../src/export/zip.js';
import { ACK_IN, FILE_IN, FILE_IN_SCRIPT, FILE_OUT, GARBAGE, LEG, PORT, RULE, RULESET, SCHEME, SEND, SIM, SUBFLOW, SUBMIT, USD, VALID, VALIDATE } from '../test/blackbox/demo-scheme.js';
import { NORMAL, SLOW, type Director, type Scene } from './record.js';
import { BANK, LAPTOP, ROOT, URLS, call, runtimeGo, runtimeSignIn, sh, stackUp, token, useStackStudio } from './stack.js';

// Runtime video 3: one bank's payments, end to end, on the laptop stack (deploy/laptop), across every console.
//
//   1. The service console: the bank's distribution A, built by the studio, installed and activated, each on a bank approver's
//      consent; the native code it carries, checked by hash; its configuration release, with the laptop's bindings of the bank's
//      file transfer, put in force on consent.
//   2. The operations console's upload page (POST /ops/api/upload, which posts to the File API's POST /files/ui): a salary file
//      starts payment P1 on A; its outgoing SIF leaves through the File API's outbox for the Central Bank's folder on the SFTP server.
//   3. The studio: the synthetic scheme uae.demo, built by the platform's designer (as demo/build.ts builds it), on the bench.
//   4. The studio: the bank licenses uae.demo (its configurator proposes, its approver approves, Issue I-205), which builds the
//      bank's distribution B; the bank accepts the scheme and enables its rail.
//   5. B installed and activated with consent beside A: A drains while P1 waits; the new values put in force.
//   6. The Central Bank's ACK, left on the SFTP server, read back by the File API and matched to P1, which settles on A; A removed.
//   7. A demo credit file uploaded: payment P2 runs on uae.demo, on B.
//   8. Around the payments: fraud alerts, the ledger accounts, the rates badge, the audit.
//
// Where a step has no screen today it is done as an operator would, by the API or a command shown in the terminal panel, and the
// voice says so. A payment on Temporal waits in real time; Director.skip cuts such a wait from the video and shows a card instead.
//
// The stack must be up, fresh, with a compose project of its own (COMPOSE_PROJECT_NAME), its secrets in the environment, and the
// demo scheme's simulator among the workers' simulators (TH_LAPTOP_WORKERS_BENCH, studio/demo/README.md).

export const NAME = 'runtime-3-full-payment-run';
export const TITLE = 'TransactHub runtime, part 3: a full payment run, end to end';
export const PACE = SLOW;

const MAKER = `${BANK}.maker`;
const CHECKER = `${BANK}.checker`;
const SVC = `${URLS.service}/service/api`;
const PLATFORM: [string, string][] = [
  ['p.port.core-banking.v1', 'p.sim.core-banking.v1'], ['p.port.sanctions.v1', 'p.sim.sanctions.v1'], ['p.port.fraud.v1', 'p.sim.fraud.v1'],
];
const DEMO: [string, string][] = [[PORT.name, SIM.name], ['uae.p.port.calendar.v1', 'uae.p.sim.calendar.v1'], ['p.port.encryption.v1', 'p.sim.encryption.v1']];
/** The laptop's bindings of the bank's file transfer, alerts and events (deploy/laptop/banks/<bank>), as the insert-only test builds them in. */
const laptopBindings = () => ['file-transfer.yaml', 'events.yaml'].map((f) => path.join(LAPTOP, 'banks', BANK, f)).filter((f) => fs.existsSync(f))
  .flatMap((f) => parseYaml(fs.readFileSync(f, 'utf8')).bindings);
/** The run's own minute, so that a file is new to the File API (it refuses a duplicate). */
const STAMP = (() => { const d = new Date(); return `${String(d.getUTCHours()).padStart(2, '0')}${String(d.getUTCMinutes()).padStart(2, '0')}`; })();
/** A synthetic salary file of the bank's employer, with no `# file:` line (the File API gives the intake map its name, I-195). */
function salaryFile(): { name: string; stem: string; text: string } {
  const sample = fs.readFileSync(path.join(ROOT, 'studio', 'fixtures', 'samples', 'wps-sif-no-file-line.sif'), 'utf8');
  const stem = `0000001234567261117${STAMP}00`;
  return { name: `${stem}.sif`, stem, text: sample.replace(',0830,', `,${STAMP},`).replace('REF-0001', `REF-${STAMP}`) };
}
const SFTP_HOME = '/home/th/upload';
const CB_SIF = `${SFTP_HOME}/cb/wps/sif`;
const CB_ACK = `${SFTP_HOME}/cb/wps/ack`;
const ls = (dir: string) => sh(`docker compose -f deploy/laptop/compose.yaml exec -T sftp sh -c 'ls -1 ${dir} 2>/dev/null || true'`).split('\n').map((l) => l.trim()).filter(Boolean);

export async function open(d: Director) {
  if (!(await stackUp())) throw new Error('The laptop stack does not answer: bring it up first (deploy/laptop/README.md)');
  if (!(process.env.TH_LAPTOP_WORKERS_BENCH ?? '').includes(SIM.name)) throw new Error(`TH_LAPTOP_WORKERS_BENCH must give the workers ${SIM.name} for ${PORT.name} (studio/demo/README.md)`);
  useStackStudio(d);
  await d.page.context().clearCookies();
  // Before the camera: the bank's two studio users, added by one administrator and approved by another (as the laptop test does).
  const admin = d.api('users.admin');
  const users = (await admin.get('/api/users?actor=users.admin')).data as any[];
  if (!users.some((u) => u.id === MAKER)) {
    const wanted = [...users.filter((u) => u.id !== CHECKER), { id: MAKER, tenant: BANK, roles: ['bank configurator'] }, { id: CHECKER, tenant: BANK, roles: ['bank approver'] }];
    const prop = await admin.post('/api/users/proposals', { actor: 'users.admin', users: wanted });
    await d.api('users.approver').post(`/api/users/proposals/${prop.data.changeSet}/approve`, { actor: 'users.approver' });
  }
  // The studio builds the bank's distributions for Temporal, with simulators, for this test environment; and the bank's current
  // distribution, A, is published, as the bank approver's approval of its last change would have built it.
  await d.api('shared.checker').put('/api/git', { actor: 'shared.checker', target: 'temporal', distributions: true, testDistributions: true });
  const versions = (await call(SVC, 'svc.engineer', 'GET', '/content/versions')).data as any[];
  if (!versions.some((v) => v.kind === 'distribution' && v.bank === BANK)) {
    const r = await d.api(CHECKER).post('/api/distributions', { actor: CHECKER, tenant: BANK, bank: BANK, engine: 'temporal', withSimulators: true });
    if (r.status !== 200) throw new Error(`distribution A: ${JSON.stringify(r.data)}`);
  }
  await d.page.goto(`${URLS.service}/`);
}

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');
  const term = (command: string, output: string) => d.terminal(command, output);
  const close = () => d.closeTerminal();
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-full-run-'));
  /** A studio page, as the user signed in. */
  const go = async (hash: string) => {
    await p().goto(`${URLS.studio}/#/${hash}`);
    await p().waitForLoadState('networkidle');
    await d.wait(d.pace.after);
  };
  const studioAs = async (user: string) => {
    if (!p().url().startsWith(URLS.studio)) { useStackStudio(d); await p().context().clearCookies(); }
    await quick(() => d.as('', user));
  };
  const status = async (re: RegExp, within = p()) => {
    for (let i = 0; i < 240; i++) {
      if ((await within.locator('[role="status"]').allTextContents()).some((t: string) => re.test(t))) return;
      await p().waitForTimeout(250);
    }
    throw new Error(`No status matching ${re}`);
  };
  /** The studio chapter goes at the normal pace: the studio's own videos show each step slowly. */
  const quick = async (fn: () => Promise<void>) => {
    const was = d.pace;
    if (!d.dry) d.pace = { ...NORMAL, steps: 8, settle: 60, after: 150, highlight: 0, typeDelay: 10, tail: 0.4 };
    try { await fn(); } finally { d.pace = was; }
  };
  // ------------------------------------------------------------ studio helpers (as demo/build.ts)
  const editor = async (kind: string, doc: any) => {
    await go(`editors?kind=${kind}`);
    await d.click(p().getByRole('button', { name: 'Start new' }));
    await d.click(p().getByRole('button', { name: 'Edit the YAML' }).first());
    await d.paste(p().getByLabel('Document YAML').first(), toYaml(doc));
    await d.click(p().getByRole('button', { name: 'Done' }).first());
  };
  const save = async () => { await d.click(p().getByRole('button', { name: /^Save draft$/ }).first()); await status(/Saved draft/); };
  const publish = async (checker = 'shared.checker', maker = d.user) => { await d.release(checker, maker); await d.wait(700); };
  const designer = async (opts: { wfKind?: string }, doc: any) => {
    await go('designer');
    await d.select(p().getByLabel('Start from'), { label: 'Blank' });
    if (opts.wfKind) await d.select(p().getByLabel('Workflow kind'), opts.wfKind);
    await d.type(p().getByLabel('Name', { exact: true }), doc.name, 10);
    await d.type(p().getByLabel('Title', { exact: true }), doc.title, 8);
    await d.click(p().getByRole('button', { name: 'Create' }));
    for (let i = 0; i < 80 && !(await p().getByLabel('Workflow YAML').inputValue()).includes(doc.name); i++) await p().waitForTimeout(100);
    await d.paste(p().getByLabel('Workflow YAML'), toYaml(doc));
    await d.wait(600);
  };
  const bench = async (target: string, tenant: string, ports: [string, string, string?][]) => {
    await go('tests');
    await d.click(p().getByRole('tab', { name: 'Ad hoc bench' }));
    await d.select(p().getByLabel('Target', { exact: true }), target);
    await d.select(p().getByLabel('Run tenant'), tenant);
    for (const [port, sim, scenario] of ports) {
      await d.click(p().getByRole('button', { name: 'Add a port' }));
      await d.select(p().getByLabel('Port', { exact: true }).last(), port);
      await d.select(p().getByLabel('Simulator', { exact: true }).last(), sim);
      await d.select(p().getByLabel('Scenario', { exact: true }).last(), scenario ?? 'accept');
    }
  };
  const withSample = async (sample: string, message?: string) => {
    await d.click(p().getByRole('tab', { name: 'Inbound sample' }));
    await d.select(p().getByLabel('Inbound sample'), sample);
    if (message) await d.select(p().getByLabel('Start message'), message);
  };
  const runBench = async () => {
    await d.click(p().getByRole('button', { name: 'Run the bench' }));
    await main().getByText('Business trace').first().waitFor({ timeout: 120_000 });
    await main().getByText('Business trace').first().scrollIntoViewIfNeeded();
    await d.wait(1500);
  };
  /** The bank's configuration set as the tenant page shows it, changed, pasted, saved; the bank approver approves it. */
  const configuration = async (change: (c: any) => void) => {
    await go('tenant');
    const cs = (await d.api(MAKER).get(`/api/tenants/${BANK}/configuration`)).data;
    const next = JSON.parse(JSON.stringify(cs.configurationSet));
    next.release = String(next.release).replace(/^(\d+)\.(\d+)\..*/, (_: string, M: string, m: string) => `${M}.${Number(m) + 1}.0`);
    change(next);
    // The page loads the stored set into the editor; paste only once it is in, or the load overwrites the paste.
    const box = p().getByLabel('Configuration set YAML');
    for (let i = 0; i < 120 && !(await box.inputValue()).includes(String(cs.configurationSet.release)); i++) await p().waitForTimeout(100);
    await d.paste(box, toYaml(next));
    const saveButton = p().getByRole('button', { name: 'Save the configuration set' });
    await d.skip('The checks run', async () => { for (let i = 0; i < 300 && !(await saveButton.isEnabled()); i++) await p().waitForTimeout(100); }, 8);
    await d.wait(1200);
    await d.click(saveButton);
    await status(/^Saved /);
    await d.wait(1200);
  };

  // ------------------------------------------------------------ runtime helpers
  const seen: Record<string, string> = {};
  /** A console's page, read again: going to the hash the page is on already would not load it again. */
  const fresh = async (base: string, hash: string) => {
    if (p().url() === `${base}/#/${hash}`) await p().goto(`${base}/`);
    await runtimeGo(d, base, hash);
  };
  const svcAs = async (who: 'service engineer' | 'bank approver', page: string) => {
    // Signing in again and again is no news: at the quick pace.
    if (seen.svc !== who || !p().url().startsWith(URLS.service)) { await quick(() => runtimeSignIn(d, URLS.service, who)); seen.svc = who; }
    await fresh(URLS.service, `service/${page}`);
  };
  const opsAs = async (who: 'operator' | 'supervisor', page: string) => {
    if (seen.ops !== who || !p().url().startsWith(URLS.ops)) { await quick(() => runtimeSignIn(d, URLS.ops, who)); seen.ops = who; }
    await fresh(URLS.ops, page);
  };
  const confirm = async (button: any, reason: string) => {
    await d.click(button);
    await d.type(p().getByRole('dialog').getByLabel(/Why/), reason, 20);
    await d.click(p().getByRole('dialog').getByRole('button', { name: /^Confirm/ }));
    await p().locator('[data-testid="result"], [role="alert"]').first().waitFor({ timeout: 60_000 });
    await d.wait(1200);
  };
  /** As the bank approver, in the service console's actions: approve the first action waiting. */
  const consent = async (reason: string) => {
    await svcAs('bank approver', 'actions');
    const row = p().locator('[data-testid="pending-row"]').first();
    await row.waitFor({ timeout: 30_000 });
    await d.point(row, 1500);
    await confirm(row.getByRole('button', { name: 'Approve' }), reason);
  };
  const versionRow = (tag: string) => p().locator(`[data-testid="version-row"][data-tag="${tag}"]`);
  const releaseRow = (id: string) => p().locator(`[data-testid="release-row"][data-release="${id}"]`);
  const versions = async () => ((await call(SVC, 'svc.engineer', 'GET', '/content/versions')).data as any[]).filter((v) => v.bank === BANK);
  const offered = async (kind: string) => (await versions()).filter((v) => v.kind === kind && v.state === 'offered');
  const pinOf = async (paymentId: string) => (await call(URLS.release, 'th-pipeline', 'GET', `/payments/pin?paymentId=${encodeURIComponent(paymentId)}`)).data;
  const until = async (what: string, ok: () => Promise<boolean>, ms = 25 * 60_000) => {
    const end = Date.now() + ms;
    while (!(await ok())) {
      if (Date.now() > end) throw new Error(`Waited ${Math.round(ms / 60000)} minutes: ${what}`);
      await new Promise((r) => setTimeout(r, 3000));
    }
  };
  /** The bank approver's configuration release with the laptop's bindings, built in the studio by its API (no screen builds one with bindings). */
  const laptopRelease = async () => {
    // Its release is the configuration set's, with build metadata of its own: the release the studio built when the set was
    // approved has the set's number already, and a published release is never rewritten. The laptop's bindings are its bindings release 1.0.0.
    const set = (await d.api(CHECKER).get(`/api/tenants/${BANK}/configuration`)).data.configurationSet;
    const r = await d.api(CHECKER).post('/api/configuration-releases', { actor: CHECKER, tenant: BANK, bank: BANK, release: `${set.release}+bindings.1.0.0`, bindings: laptopBindings() });
    if (r.status !== 200) throw new Error(`configuration release: ${JSON.stringify(r.data)}`);
    return r.data as { tag: string; releaseId: string; files: number };
  };
  /** Install a version from the service console: the engineer asks, the bank approver consents. */
  const install = async (tag: string, reason: string) => {
    await svcAs('service engineer', 'content');
    await confirm(versionRow(tag).getByRole('button', { name: 'Install' }), reason);
    await consent(`Agreed: ${reason.split(':')[0]} (synthetic)`);
    await svcAs('service engineer', 'content');
  };
  const activate = async (releaseId: string, reason: string) => {
    await svcAs('service engineer', 'content');
    await confirm(releaseRow(releaseId).getByRole('button', { name: 'Activate' }), reason);
    await consent(`Agreed: ${reason.split(':')[0]} (synthetic)`);
    await svcAs('service engineer', 'content');
  };
  /** The operator's upload page: the message, the file, Upload; the answer, and the payment it started. */
  const upload = async (message: RegExp, name: string, text: string) => {
    await opsAs('operator', 'upload');
    const file = path.join(work, name);
    fs.writeFileSync(file, text);
    const sel = p().getByLabel('Message');
    // The page asks the console for the messages the active distribution receives: wait for the answer.
    for (let i = 0; i < 100 && !(await sel.locator('option').count()); i++) {
      const err = p().locator('[role="alert"]');
      if (await err.count()) throw new Error(`The upload page: ${await err.first().textContent()}`);
      await p().waitForTimeout(200);
    }
    const value = await sel.locator('option').evaluateAll((os: any[], src: string) => os.find((o) => new RegExp(src).test(o.textContent))?.value, message.source);
    if (!value) throw new Error(`The upload page offers no message like ${message}: ${(await sel.locator('option').allTextContents()).join('; ')}`);
    await d.select(sel, value);
    const input = p().getByLabel('File');
    await d.hover(input, 400);
    await input.setInputFiles(file);
    await d.wait(800);
    await d.click(p().getByRole('button', { name: 'Upload' }));
    const answer = p().getByTestId('upload-answer');
    await answer.waitFor({ timeout: 120_000 });
    await d.point(answer, 2000);
    const link = p().getByTestId('upload-payment');
    if (!(await link.count())) throw new Error(`The upload started no payment: ${await answer.textContent()}`);
    return (await link.textContent())!.replace(/^Payment /, '').trim();
  };
  /**
   * A payment's page, from the payments list: the row of its scheme. The upload's answer links the File API's payment id, which
   * the operations console does not know for a file (a bulk): the store accepts the file under a bulk id of its own (Issue I-286).
   */
  const openPayment = async (scheme: string) => {
    await opsAs('operator', 'payments');
    const row = p().locator('[data-testid="payment-row"]', { hasText: scheme }).first();
    for (let i = 0; i < 60 && !(await row.count()); i++) { await p().waitForTimeout(1000); await fresh(URLS.ops, 'payments'); }
    await d.point(row, 1500);
    await d.click(row.locator('a').first());
    await p().locator('main h1, main h2').first().waitFor({ timeout: 15_000 });
    await d.wait(1200);
  };
  /** The files the File API keeps for a payment, each with its hash, as the channel reads them (GET /files). */
  const keptFiles = async (paymentId: string) => {
    const r = await call(URLS.files, 'th-channel', 'GET', `/files?tenant=${BANK}&paymentId=${encodeURIComponent(paymentId)}`);
    const rows = (Array.isArray(r.data) ? r.data : []).map((f: any) => `${String(f.direction).padEnd(4)} ${String(f.kind).padEnd(18)} ${String(f.fileName).padEnd(42)} ${String(f.sha256 ?? f.hash ?? '').slice(0, 23)}...  ${f.verified ? 'signed' : ''}`);
    await term(`curl -s 'http://localhost:8485/files?tenant=${BANK}&paymentId=${paymentId}'   # as the channel`, rows.join('\n') || JSON.stringify(r.data).slice(0, 1500));
  };
  const detailTab = async (name: RegExp) => {
    const t = p().getByRole('tab', { name });
    if (await t.count()) await d.click(t.first());
  };
  const state: { A?: any; B?: any; R1?: any; R3?: any; P1?: string; P2?: string; sif?: { name: string; stem: string; text: string }; sent?: string } = {};

  return [
    // ================================================================== 0
    {
      card: ['Part 3: a full payment run', 'One bank, from the service console to the Central Bank and back, and a new scheme from the studio.'],
      say: 'In this video we follow one bank\'s payments all the way through TransactHub, on the laptop stack from part 1. We install the bank\'s distribution, put its configuration in force, and send a salary file to the Central Bank. Then we build a new scheme in the studio, license it to the bank, and pay through it. Everything is synthetic: no real customer, no real account.',
    },
    // ================================================================== 1. install
    {
      card: ['1. Install, activate, put in force', 'In the service console, each step with a bank approver\'s consent'],
      say: 'A service engineer opens the service console. The bank\'s distribution, A, is offered on GitHub. A distribution is a signed, complete copy of everything the bank runs, built by the studio. Difference shows what it would change. Nothing is active yet, so everything is new.',
      do: async () => {
        await svcAs('service engineer', 'content');
        state.A = (await offered('distribution')).pop();
        if (!state.A) throw new Error('No distribution of the bank is offered');
        await d.point(versionRow(state.A.tag), 1500);
        await d.click(versionRow(state.A.tag).getByRole('button', { name: 'Difference' }));
        await d.point(p().getByTestId('diff'), 2000);
      },
    },
    {
      say: 'Install asks for a reason, and then it waits. The engineer who asks may not consent. A bank approver, another person, must.',
      do: async () => { await confirm(versionRow(state.A.tag).getByRole('button', { name: 'Install' }), `CR-DEMO-001: install ${state.A.release} (synthetic)`); },
    },
    {
      say: 'The bank approver signs in, sees what waits, and approves it, with a reason. The runtime checks the signature, and stores the distribution. It only ever adds rows: nothing is updated or deleted.',
      do: async () => { await consent('Agreed: CR-DEMO-001 (synthetic)'); await svcAs('service engineer', 'content'); await d.point(releaseRow(state.A.releaseId), 1500); },
    },
    {
      say: 'The distribution also carries native code: compiled modules, one for the platform and one for the WPS scheme. The runtime\'s own image holds none. The signed manifest lists every file with its hash. When the distribution is installed, and again when the native host loads a module, each file is checked against its hash. A file that does not match is refused.',
      do: async () => {
        const r = await fetch(`${URLS.release}/distributions/package?id=${encodeURIComponent(state.A.releaseId)}`, { headers: { authorization: `Bearer ${await token('th-pipeline')}` } });
        const files = Object.fromEntries(unzip(Buffer.from(await r.arrayBuffer())).map((e) => [e.name, e.content]));
        const manifest = parseYaml(files['manifest.yaml'].toString());
        const lines: string[] = [`native contract ${manifest.native?.contract}`];
        for (const m of manifest.native?.modules ?? []) {
          lines.push('', `module ${m.scope}: ${m.assembly}, serves ${(m.activities ?? []).length} activities`);
          for (const f of m.files ?? []) {
            const bytes = Buffer.from(files[f.path].toString().trim(), 'base64');
            const sha = `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
            lines.push(`  ${f.name.padEnd(42)} ${String(bytes.length).padStart(7)} bytes  ${f.sha256.slice(0, 23)}...  ${sha === f.sha256 ? 'matches' : 'DOES NOT MATCH'}`);
          }
        }
        await term(`curl -s http://localhost:8481/distributions/package?id=${state.A.releaseId} | unzip -p - manifest.yaml   # native, and each file's hash checked`, lines.join('\n'));
      },
    },
    {
      say: 'Installing is not using. The engineer activates A, and the approver consents again. From now on, new payments of this bank take distribution A.',
      do: async () => { await close(); await activate(state.A.releaseId, `CR-DEMO-001: activate ${state.A.release} (synthetic)`); await d.point(releaseRow(state.A.releaseId), 2000); },
    },
    {
      say: 'Next, the bank\'s values and bindings: its configuration release. It carries values, never code. On this laptop every partner of the bank is one SFTP server in the stack, so the bank approver builds the release with the laptop\'s file transfer bindings. The studio has no screen for that yet, so it is done with the studio\'s API.',
      do: async () => {
        state.R1 = await laptopRelease();
        await term(`curl -s -X POST http://localhost:4317/api/configuration-releases -d '{"bank":"${BANK}","bindings": <deploy/laptop/banks/${BANK}/file-transfer.yaml, events.yaml>}'   # as ${CHECKER}`,
          JSON.stringify({ releaseId: state.R1.releaseId, tag: state.R1.tag, files: state.R1.files, bindings: laptopBindings().map((b: any) => `${b.port} -> ${b.adapter}`) }, null, 2));
      },
    },
    {
      say: 'Installing a configuration release and putting it in force are two steps. Installing stores it, and checks every value against the bounds the distribution declares. Putting it in force makes its values and bindings the ones payments use, and that needs the bank approver\'s consent. Asked for here, the one consent does both. The release now says: in force.',
      do: async () => {
        await close();
        await svcAs('service engineer', 'content');
        await d.point(versionRow(state.R1.tag), 1200);
        await install(state.R1.tag, `CR-DEMO-002: put ${state.R1.releaseId} in force (synthetic)`);
        await d.point(releaseRow(state.R1.releaseId), 2500);
      },
    },
    // ================================================================== 2. a salary file in
    {
      card: ['2. A salary file, uploaded', 'On the operations console\'s upload page, through the File API'],
      say: 'Now a payment. An operator opens the operations console\'s upload page. The file goes to the File API as the operator\'s own upload. The File API checks it, keeps it with its hash, and starts its payment on the active distribution. We upload a synthetic salary file.',
      do: async () => {
        state.sif = salaryFile();
        state.P1 = await upload(/uae\.wps\.sif/, state.sif.name, state.sif.text);
      },
    },
    {
      say: 'Here is the payment, in the payments list. Its timeline is a list of facts, one per step, each chained to the one before. The file was read into the ISO 20022 model, checked, screened, and the salary file for the Central Bank, the SIF, was written.',
      do: async () => {
        await openPayment('uae.wps');
        await detailTab(/^Timeline/);
        await d.scroll(400);
      },
    },
    {
      say: 'The SIF leaves through the File API\'s outbox. The File API delivers it to the Central Bank\'s folder on the SFTP server. Here it is. The payment now waits for the Central Bank\'s answer.',
      do: async () => {
        await until('the SIF on the SFTP server', async () => (ls(CB_SIF).some((n) => n.toUpperCase() === `${state.sif!.stem}.SIF`)), 10 * 60_000);
        state.sent = ls(CB_SIF).find((n) => n.toUpperCase() === `${state.sif!.stem}.SIF`);
        await term(`sftp -P 12222 th@127.0.0.1   # ls upload/cb/wps/sif`, `${sh(`docker compose -f deploy/laptop/compose.yaml exec -T sftp ls -l ${CB_SIF}`)}\n\n$ head -c 160 upload/cb/wps/sif/${state.sent}\n${sh(`docker compose -f deploy/laptop/compose.yaml exec -T sftp head -c 160 ${CB_SIF}/${state.sent}`)}`);
      },
    },
    // ================================================================== 3. a new scheme
    {
      card: ['3. A new scheme, in the studio', 'The synthetic scheme uae.demo, built by the platform\'s designer'],
      say: 'While it waits, the platform\'s designer builds a new scheme in the studio: a made-up one, called U A E demo. We go quickly here. Video 2 of the studio builds it step by step. First its test data: a good file, a file in dollars, and a file that is not a demo file at all.',
      do: async () => quick(async () => {
        await close();
        await studioAs('shared.designer');
        await go('tests');
        await d.click(p().getByRole('tab', { name: 'Ad hoc bench' }));
        await d.click(p().getByRole('tab', { name: 'Inbound sample' }));
        for (const [name, content] of [['demo-credit-file-valid.csv', VALID], ['demo-credit-file-usd.csv', USD], ['demo-credit-file-garbage.csv', GARBAGE]]) {
          await d.click(p().getByRole('button', { name: 'Add a sample file' }));
          await d.type(p().getByLabel('Sample file name'), name, 10);
          await d.paste(p().getByLabel('Sample content'), content);
          await d.click(p().getByRole('button', { name: /^Save the sample as / }));
        }
      }),
    },
    {
      say: 'Then the port to the demo clearing house, and the simulator that stands in for it in tests. Then the checks: a rule that a demo file is in dirhams, its rule set, and the activity that runs them. A checker, never the maker, approves each change.',
      do: async () => quick(async () => {
        await editor('port', PORT); await save(); await editor('simulator', SIM); await save(); await publish();
        await editor('rule', RULE); await save(); await publish();
        await editor('ruleset', RULESET); await save(); await publish();
        await editor('activity', VALIDATE); await save(); await publish();
      }),
    },
    {
      say: 'The maps: one reads the file into the ISO 20022 model, with a small script. One writes the file for the clearing house, and one reads its acknowledgement. Then the send.',
      do: async () => quick(async () => {
        await editor('map', FILE_IN); await save();
        await d.paste(p().getByLabel('Script source'), FILE_IN_SCRIPT);
        await d.click(p().getByRole('button', { name: /^Save the script as / }));
        await status(/Saved uae\.demo\.map\.file-in\.v1\.ts/);
        await publish();
        await editor('map', FILE_OUT); await save(); await publish();
        await editor('map', ACK_IN); await save(); await publish();
        await editor('activity', SEND); await save(); await publish();
      }),
    },
    {
      say: 'Then the leg: check the file, check each account, write the file, and submit it, waiting for the answer. The scheme\'s dispatch table connects the leg to the platform\'s corporate workflow: a demo credit file starts a corporate payment, with this leg in the scheme slot. They refer to one another, so the checker approves them as one change set.',
      do: async () => quick(async () => {
        await editor('composite', SUBMIT); await save();
        await designer({ wfKind: 'subflow' }, SUBFLOW); await save();
        await designer({ wfKind: 'leg' }, LEG); await save();
        await d.point(main().locator('.cv-steps').first(), 1500);
        await editor('scheme', SCHEME); await save();
        await main().getByText('Scheme and dispatch table').last().scrollIntoViewIfNeeded();
        await d.wait(1200);
        await publish();
      }),
    },
    {
      say: 'We test the leg on the studio\'s bench, against the clearing house\'s simulator. The file is checked, both accounts pass, the file is submitted and acknowledged, and the leg settles.',
      do: async () => quick(async () => {
        await bench(LEG.name, 'owner', DEMO);
        await withSample('samples/demo-credit-file-valid.csv');
        await runBench();
      }),
    },
    // ================================================================== 4. the bank licenses it
    {
      card: ['4. The bank licenses the scheme', 'Its configurator proposes, its approver approves'],
      say: 'A bank pays through a scheme only once it licenses it. The bank\'s configurator opens the bank\'s page and proposes a licence for U A E demo. The licence is a change to the bank\'s lock file, the list of what its distribution carries, so it waits for the bank\'s approver.',
      do: async () => {
        await studioAs(MAKER);
        await go('bank');
        const box = p().locator('[aria-label="Schemes licensed"]');
        await d.point(box, 1500);
        await d.click(box.locator('tr', { hasText: 'uae.demo' }).getByRole('button', { name: 'License uae.demo' }));
        await d.point(box.getByTestId('licence-draft'), 1500);
      },
    },
    {
      say: 'The bank approver, another person, signs in and approves the licence. The approval builds the bank\'s next distribution, B. It carries the new scheme, and is published to GitHub with a tag.',
      do: async () => {
        await studioAs(CHECKER);
        await go('bank');
        const box = p().locator('[aria-label="Schemes licensed"]');
        await d.click(box.getByRole('button', { name: 'Approve the licence' }));
        await d.skip('The approval builds distribution B', async () => {
          for (let i = 0; ; i++) {
            const t = (await box.locator('[role="status"], .error').allTextContents()).join(' ');
            if (/approved in change set/.test(t)) break;
            if (/Refused/.test(t) || i > 480) throw new Error(`The licence: ${t}`);
            await p().waitForTimeout(250);
          }
        }, 8);
        await d.point(box, 2000);
      },
    },
    {
      say: 'Then the bank\'s values: it accepts the scheme\'s release and enables its rail, in its configuration set. The checks run as we type, and every value must stay inside its bounds. The approver approves it.',
      do: async () => {
        await studioAs(MAKER);
        await configuration((c) => {
          if (!c.accepts.some((a: any) => a.scope === 'uae.demo')) c.accepts.push({ scope: 'uae.demo', release: '1.0.0' });
          const rails = c.values.find((v: any) => v.parameter === 'p.table.rail-choice.v1#enabledRails');
          if (rails && !rails.value.includes('uae.demo')) rails.value.push('uae.demo');
        });
        await studioAs(CHECKER);
        await go('governance');
        await d.click(p().locator('tr', { hasText: `config:${BANK}` }).first().getByRole('button', { name: 'Approve as version' }));
        await d.skip('The approval builds a configuration release', async () => {
          for (let i = 0; ; i++) {
            const t = (await p().locator('[role="status"]').allTextContents()).join(' ');
            if (/approved as a version/.test(t)) break;
            if (/Refused/.test(t) || i > 480) throw new Error(`The values: ${t}`);
            await p().waitForTimeout(250);
          }
        }, 8);
      },
    },
    {
      say: 'A whole payment for the bank on the studio\'s bench: a demo credit file starts the corporate workflow with the bank\'s values, the rail is the new scheme, and the new leg runs in its slot.',
      do: async () => {
        await studioAs(MAKER);
        await bench('p.wf.corporate.v1', BANK, [...PLATFORM, ...DEMO]);
        await withSample('samples/demo-credit-file-valid.csv', 'uae.demo.credit-file');
        await runBench();
      },
    },
    // ================================================================== 5. B beside A
    {
      card: ['5. Side by side', 'B is activated; A drains while its payment waits'],
      say: 'Back in the service console, distribution B is offered. The engineer installs it, and the approver consents. Then the engineer activates B, and the approver consents again. Activating B moves A to draining, in the same step.',
      do: async () => {
        await until('distribution B offered', async () => (await offered('distribution')).length > 0, 120_000);
        state.B = (await offered('distribution')).pop();
        await install(state.B.tag, `CR-DEMO-003: install ${state.B.release} (synthetic)`);
        await activate(state.B.releaseId, `CR-DEMO-003: activate ${state.B.release} (synthetic)`);
        await d.point(p().getByRole('table', { name: 'Releases' }), 2500);
      },
    },
    {
      say: 'A cannot be removed now. One payment pinned to it is still open: our salary payment, waiting for the Central Bank. The button stays off. New payments take B. Our payment stays on A until it ends.',
      do: async () => { await d.hover(releaseRow(state.A.releaseId).getByRole('button', { name: 'Remove' }), 2500); },
    },
    {
      say: 'The bank\'s new values go the same way. Approving them in the studio built a configuration release. On the laptop, the approver builds one more with the laptop\'s bindings on top, and that is the one the engineer installs. It is checked against both distributions, and put in force with the approver\'s consent. Nothing is redeployed for a value.',
      do: async () => {
        state.R3 = await laptopRelease();
        await install(state.R3.tag, `CR-DEMO-004: put ${state.R3.releaseId} in force (synthetic)`);
        await d.point(releaseRow(state.R3.releaseId), 2000);
      },
    },
    // ================================================================== 6. the answer
    {
      card: ['6. The Central Bank answers', 'An ACK on the SFTP server, read back by the File API'],
      say: 'Now we play the Central Bank. It leaves an acknowledgement, an ACK, in its answer folder on the SFTP server, named after the salary file. The File API picks it up, matches it to the payment by the salary file\'s name, and hands it to the payment.',
      do: async () => {
        const sif = state.sent!;
        const ack = `${state.sif!.stem}S26000000001.ACK`;
        const text = `AHR,ACCEPTED,${sif}\nATR,ACCEPTED,2\n`;
        fs.writeFileSync(path.join(work, ack), text);
        const out = sh(`docker compose -f deploy/laptop/compose.yaml exec -T -u 1001 sftp sh -c 'mkdir -p ${CB_ACK} ${SFTP_HOME}/cb/wps/writing && cat > ${SFTP_HOME}/cb/wps/writing/${ack} && mv ${SFTP_HOME}/cb/wps/writing/${ack} ${CB_ACK}/${ack} && ls -l ${CB_ACK}' < ${path.join(work, ack)}`);
        await term(`sftp -P 12222 th@127.0.0.1   # put ${ack} upload/cb/wps/ack/`, `${text}\n$ ls -l upload/cb/wps/ack\n${out}`);
      },
    },
    {
      say: 'We let the payment read it.',
      do: async () => {
        await d.skip('The payment reads the ACK and runs to its end, on A', async () => { await until('P1 ended', async () => !!(await pinOf(state.P1!))?.ended); });
        const endedP1 = (await pinOf(state.P1!)).ended;
        if (endedP1 !== 'settled') throw new Error(`P1 ended ${endedP1}, not settled`);
        await close();
        await openPayment('uae.wps');
      },
    },
    {
      say: 'The Central Bank accepted the file, so the payment settled, on A, where it started. The File API keeps every file of the payment, each with its hash: the salary file that came in, the SIF that went out, and the ACK that came back. The payment\'s page does not list them yet, because the File API and the store know the payment by two different ids. That is recorded as an issue.',
      do: async () => { await keptFiles(state.P1!); },
    },
    {
      say: 'With no payment open on it, A is drained. The engineer asks to remove it, and the approver consents. The runtime still refuses, and says why: a late answer could still arrive for a payment of A, so A is kept until its longest declared wait has passed, thirty days here. Even then, its package stays in the store and on GitHub, as a record.',
      do: async () => {
        await close();
        await svcAs('service engineer', 'content');
        const remove = releaseRow(state.A.releaseId).getByRole('button', { name: 'Remove' });
        await until('A shows no open payment', async () => { await fresh(URLS.service, 'service/content'); return remove.isEnabled(); }, 120_000).catch(() => undefined);
        if (!(await remove.isEnabled())) throw new Error('A still counts an open payment');
        await d.point(releaseRow(state.A.releaseId), 1200);
        await confirm(remove, `CR-DEMO-005: remove ${state.A.release}, drained (synthetic)`);
        await consent('Agreed: CR-DEMO-005 (synthetic)');
        const answer = p().locator('[role="alert"], [data-testid="result"]').first();
        const text = (await answer.textContent()) ?? '';
        if (!/longest declared wait|removed/.test(text)) throw new Error(`The removal answered: ${text}`);
        await d.point(answer, 3500);
      },
    },
    // ================================================================== 7. a payment on the new scheme
    {
      card: ['7. A payment on the new scheme', 'A demo credit file, on distribution B'],
      say: 'Last, a payment on the new scheme. The operator uploads a demo credit file. The upload page offers the messages the active distribution receives, and that is now B, with U A E demo.',
      do: async () => {
        const id = `DEMO-${STAMP}`;
        state.P2 = await upload(/uae\.demo\.credit-file/, `${id}.csv`, VALID.replace(/DEMO-0001/g, id));
      },
    },
    {
      say: 'It runs the same corporate workflow, with the demo leg in the scheme slot. On this laptop the clearing house is its simulator. We let it run to its end.',
      do: async () => {
        await openPayment('uae.demo');
        await d.skip('The demo payment runs to its end, on B', async () => { await until('P2 ended', async () => !!(await pinOf(state.P2!))?.ended); });
        const endedP2 = (await pinOf(state.P2!)).ended;
        if (endedP2 !== 'settled') throw new Error(`P2 ended ${endedP2}, not settled`);
        await openPayment('uae.demo');
        await detailTab(/^Legs/);
      },
    },
    {
      say: 'It settled, on B, through U A E demo: a scheme built in the studio a few minutes ago, licensed by the bank, and paid in the runtime, with nothing redeployed but signed content.',
      do: async () => {
        await opsAs('operator', 'payments');
        await d.point(p().locator('[data-testid="payment-row"]').first(), 2500);
      },
    },
    // ================================================================== 8. around the payments
    {
      card: ['8. Around the payments', 'Fraud alerts, the ledger accounts, rates, and the audit'],
      say: 'A few more pages of the operations console. Fraud alerts: a payment the fraud check finds suspect becomes an alert here, for staff to take, note and close. Our payments raised none.',
      do: async () => { await opsAs('supervisor', 'alerts'); await d.wait(1500); },
    },
    {
      say: 'Ledger accounts: the bank\'s own accounts that payments post against, as its configuration in force names them. They are read only here. They change in the studio, as a new configuration release.',
      do: async () => { await opsAs('supervisor', 'ledger'); await d.wait(1500); },
    },
    {
      say: 'If the bank\'s exchange rates are older than it allows, a red badge says "Rates stale" in the top bar, and a payment that needs a rate is refused rather than paid at an old one.',
      do: async () => {
        const badge = p().getByTestId('rates-stale');
        if (await badge.count()) await d.point(badge, 2500); else await d.point(p().locator('header').first(), 2000);
      },
    },
    {
      say: 'And the audit. Every request, every consent and every upload is a fact: who, when and why. The operations console\'s audit reads them, and so does the service console\'s event log.',
      do: async () => {
        await opsAs('supervisor', 'audit');
        await d.wait(2000);
        await svcAs('service engineer', 'logs');
        await d.wait(1500);
      },
    },
    {
      card: ['That is the whole run', 'Install, consent, upload, the Central Bank and back, a new scheme licensed and paid.'],
      say: 'That is the whole run. A distribution installed and activated, its native code checked, its configuration put in force, each with a second person\'s consent. A salary file uploaded, sent to the Central Bank and answered. A new scheme built in the studio, licensed by the bank, put beside the first distribution while that one drained, and paid.',
    },
  ];
}
