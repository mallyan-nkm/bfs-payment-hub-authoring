import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { parseYaml, toYaml } from '../src/core/yaml.js';
import { NORMAL, SLOW, type Director, type Scene } from './record.js';
import { BANK, LAPTOP, ROOT, URLS, call, mask, runtimeGo, runtimeSignIn, sh, stackUp, useStackStudio } from './stack.js';

// Video 4, end to end, on the laptop stack (deploy/laptop) on Fluent UI 2, for one bank (ADIB by default):
//
//   1. The test bench sends a WPS salary file: the bank's run on Temporal, the default engine, beside the interpreter (Issue I-331),
//      with the business date; then the same kind of file into the runtime through the File API, as the bank's channel sends one.
//   2. The workflow, step by step: the studio's debugger walks the platform workflow and the WPS leg (intake, checks, business date
//      and cut-off, screening, the SIF rendered and sent, the ACK read, posting, settlement); then the real payment's timeline in the
//      operations console, its SIF on the Central Bank's SFTP folder, the ACK left there and matched, the payment settled.
//   3. A business failure: the Central Bank refuses a file (NAK); the operations console shows the status, the reason and the line's
//      error; the bank sends a corrected file, which is accepted.
//   4. A technical failure: the SFTP partner is down; the File API cannot deliver the SIF, the circuit breaker opens; the service
//      console shows it (health, the error dictionary's runbook, the logs); the partner comes back, the engineer resends the kept
//      file with the approver's consent (Issue I-333), the ACK comes back and the payment settles.
//   5. A business-rule change: the bank configurator changes the cut-off rule's dial in the studio, the bank approver approves, the
//      configuration release is put in force with consent in the service console; the same file runs again and counts for the next
//      business day.
//   6. A new hook: the configurator binds the bank's employer AML check at the hook point after validation, the approver approves
//      and builds the bank's next distribution; it is installed and activated with consent, the release put in force; the file runs
//      again and the hook's step is on the payment's timeline (Issue I-335).
//
// The stack must be up with its secrets in the environment (deploy/laptop/README.md). A recording, or a dry run with
// TH_DEMO_RESET=1, starts it fresh (down -v, then up) before the camera. A payment on Temporal runs in real time; Director.skip cuts a
// wait from the video and shows a card instead.

export const NAME = '4-end-to-end';
export const TITLE = 'TransactHub: a salary file end to end, its failures, a rule change and a new hook';
export const PACE = SLOW;

const MAKER = `${BANK}.maker`;
const CHECKER = `${BANK}.checker`;
const SVC = `${URLS.service}/service/api`;
const C = 'docker compose -f deploy/laptop/compose.yaml';
const SFTP_HOME = '/home/th/upload';
const CB_SIF = `${SFTP_HOME}/cb/wps/sif`;
const CB_ACK = `${SFTP_HOME}/cb/wps/ack`;
const CUT_OFF = 'p.decide.cut-off.v1#cutOffs';
const AML_POINT = 'p.seg.check-and-screen.v1#after-validate';
const AML_HOOK = 'b.hook.employer-aml-check.v1';
/**
 * The laptop's bindings of the bank's file transfer (deploy/laptop/banks/<bank>), as the insert-only test builds them in. Not its
 * events: the brokers of the `events` profile are not started for this video, and their breakers would show open.
 */
const laptopBindings = () => ['file-transfer.yaml'].map((f) => path.join(LAPTOP, 'banks', BANK, f)).filter((f) => fs.existsSync(f))
  .flatMap((f) => parseYaml(fs.readFileSync(f, 'utf8')).bindings);
/** The time of day in Dubai, HH:mm, a number of minutes from now. */
const dubai = (minutes = 0) => new Date(Date.now() + (4 * 60 + minutes) * 60_000).toISOString().slice(11, 16);
/** The bank's cut-off for outward WPS files. */
const cutOff = (at: string) => [{ scheme: 'uae.wps', direction: 'outward', cutOff: at, timeZone: 'Asia/Dubai', afterCutOff: 'next-business-day' }];
/** A configuration set's next minor release. */
const bump = (r: string) => String(r).replace(/^(\d+)\.(\d+)\..*/, (_: string, M: string, m: string) => `${M}.${Number(m) + 1}.0`);

let seq = 0;
/**
 * A synthetic salary file of the bank's employer, with no `# file:` line (the File API gives the intake map its name, I-195), and a
 * file reference of its own: the File API refuses a file it has taken before. `bad` makes its control record disagree with its line.
 */
function salaryFile(): { name: string; stem: string; text: string } {
  const sample = fs.readFileSync(path.join(ROOT, 'studio', 'fixtures', 'samples', 'wps-sif-no-file-line.sif'), 'utf8');
  const t = new Date();
  const stamp = `${String(t.getUTCHours()).padStart(2, '0')}${String(t.getUTCMinutes()).padStart(2, '0')}`;
  const n = String(++seq).padStart(2, '0');
  const stem = `0000001234567261117${stamp}${n}`;
  return { name: `${stem}.sif`, stem, text: sample.replace(',0830,', `,${stamp},`).replace('REF-0001', `REF-${stamp}${n}`) };
}

/** The stack down with its volumes and up again, fresh: the SFTP server first, its host keys pinned (deploy/laptop/README.md). */
function resetStack() {
  sh(`${C} --profile flowable down -v --remove-orphans`, { timeout: 600_000 });
  sh(`${C} up -d --wait sftp`, { timeout: 600_000 });
  process.env.TH_LAPTOP_SFTP_HOST_KEY = ['ed25519', 'rsa'].map((k) => sh(`${C} exec -T sftp ssh-keygen -lf /etc/ssh/ssh_host_${k}_key.pub`).split(' ')[1]).join(',');
  const up = sh(`${C} up -d --wait`, { timeout: 900_000 });
  if (/error|unhealthy/i.test(up.split('\n').slice(-3).join('\n'))) throw new Error(`The stack did not come up: ${up.slice(-2000)}`);
}

export async function open(d: Director) {
  // The set-up is not shown: it is cut from the video, which starts at the first scene's card.
  const t0 = d.now();
  if (!d.dry || process.env.TH_DEMO_RESET === '1') resetStack();
  for (let i = 0; i < 60 && !(await stackUp()); i++) await new Promise((r) => setTimeout(r, 2000));
  if (!(await stackUp())) throw new Error('The laptop stack does not answer: bring it up first (deploy/laptop/README.md)');
  useStackStudio(d);
  await d.page.context().clearCookies();
  // Before the camera, as the installation video showed on screen: the bank's two studio users, its first distribution built and
  // published by the studio, installed and activated, and its configuration release with the laptop's bindings put in force, each
  // with a bank approver's consent. The bank's cut-off for salary files starts late in the evening, so the first payments are
  // taken for today whenever the video is recorded.
  const admin = d.api('users.admin');
  const users = (await admin.get('/api/users?actor=users.admin')).data as any[];
  if (!users.some((u) => u.id === MAKER)) {
    const wanted = [...users.filter((u) => u.id !== CHECKER), { id: MAKER, tenant: BANK, roles: ['bank configurator'] }, { id: CHECKER, tenant: BANK, roles: ['bank approver'] }];
    const prop = await admin.post('/api/users/proposals', { actor: 'users.admin', users: wanted });
    await d.api('users.approver').post(`/api/users/proposals/${prop.data.changeSet}/approve`, { actor: 'users.approver' });
  }
  await d.api('shared.checker').put('/api/git', { actor: 'shared.checker', target: 'temporal', distributions: true, testDistributions: true });
  const consentAll = async () => {
    for (const p of (await call(SVC, 'bank.approver', 'GET', '/actions/pending')).data as any[]) {
      const r = await call(SVC, 'bank.approver', 'POST', `/actions/${encodeURIComponent(p.data.action)}/decision`, { approve: true, reason: 'Agreed: set-up before the video (synthetic)' });
      if (r.data?.status !== 'done') throw new Error(`set-up: ${JSON.stringify(r.data)}`);
    }
  };
  const versions = async () => ((await call(SVC, 'svc.engineer', 'GET', '/content/versions')).data as any[]).filter((v) => v.bank === BANK);
  if (!(await versions()).some((v) => v.kind === 'distribution')) {
    const r = await d.api(CHECKER).post('/api/distributions', { actor: CHECKER, tenant: BANK, bank: BANK, engine: 'temporal', withSimulators: true });
    if (r.status !== 200) throw new Error(`distribution A: ${JSON.stringify(r.data)}`);
  }
  const A = (await versions()).find((v) => v.kind === 'distribution')!;
  if (A.state === 'offered') {
    await call(SVC, 'svc.engineer', 'POST', '/content/install', { tag: A.tag, reason: 'CR-E2E-001: install the first distribution (synthetic)' });
    await consentAll();
  }
  if (!(await versions()).some((v) => v.kind === 'distribution' && v.state === 'active')) {
    await call(SVC, 'svc.engineer', 'POST', '/actions', { action: 'activate', target: A.releaseId, reason: 'CR-E2E-001: activate it (synthetic)' });
    await consentAll();
  }
  if (!(await versions()).some((v) => v.kind === 'configuration' && v.state === 'in-force')) {
    const cs = (await d.api(MAKER).get(`/api/tenants/${BANK}/configuration`)).data.configurationSet;
    const next = JSON.parse(JSON.stringify(cs));
    next.release = bump(next.release);
    next.values = next.values.filter((v: any) => v.parameter !== CUT_OFF);
    next.values.push({ parameter: CUT_OFF, value: cutOff('23:30'), validFrom: '2026-11-24' });
    const saved = await d.api(MAKER).post(`/api/tenants/${BANK}/configuration`, { yaml: toYaml(next) });
    const pub = await d.api(CHECKER).post('/api/publish', { keys: [saved.data.draft.key], tenant: BANK });
    if (pub.status !== 200) throw new Error(`the first configuration: ${JSON.stringify(pub.data)}`);
    const r = await d.api(CHECKER).post('/api/configuration-releases', { actor: CHECKER, tenant: BANK, bank: BANK, release: `${next.release}+bindings.1.0.0`, bindings: laptopBindings() });
    if (r.status !== 200) throw new Error(`the first configuration release: ${JSON.stringify(r.data)}`);
    await call(SVC, 'svc.engineer', 'POST', '/content/install', { tag: r.data.tag, reason: 'CR-E2E-002: put the laptop configuration in force (synthetic)' });
    await consentAll();
  }
  await d.page.goto(`${URLS.testbench}/`);
  if (!d.dry && d.now() - t0 > 5) d.cuts.push({ from: t0 + 0.2, to: d.now() });
}

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');
  const term = (command: string, output: string) => d.terminal(command, mask(output));
  const close = () => d.closeTerminal();
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-e2e-'));
  const quick = async (fn: () => Promise<void>) => {
    const was = d.pace;
    if (!d.dry) d.pace = { ...NORMAL, steps: 8, settle: 60, after: 150, highlight: 0, typeDelay: 10, tail: 0.4 };
    try { await fn(); } finally { d.pace = was; }
  };
  const expectText = async (re: RegExp, within = main(), seconds = 30) => {
    for (let i = 0; i < seconds * 4; i++) {
      if (re.test(await within.innerText().catch(() => ''))) return;
      await p().waitForTimeout(250);
    }
    throw new Error(`The page does not show ${re}: ${(await within.innerText().catch(() => '')).slice(0, 600)}`);
  };
  const until = async (what: string, ok: () => Promise<boolean>, ms = 10 * 60_000) => {
    const end = Date.now() + ms;
    while (!(await ok())) {
      if (Date.now() > end) throw new Error(`Waited ${Math.round(ms / 60000)} minutes: ${what}`);
      await new Promise((r) => setTimeout(r, 2000));
    }
  };

  // ------------------------------------------------------------ the studio
  const go = async (hash: string) => {
    await p().goto(`${URLS.studio}/#/${hash}`);
    await p().waitForLoadState('networkidle');
    await d.wait(d.pace.after);
  };
  const studioAs = async (user: string) => {
    if (!p().url().startsWith(URLS.studio)) { useStackStudio(d); await p().context().clearCookies(); }
    await quick(() => d.as('', user));
  };
  const status = async (re: RegExp) => {
    for (let i = 0; i < 480; i++) {
      const t = (await p().locator('[role="status"]').allTextContents()).join(' ');
      if (re.test(t)) return;
      if (/Refused/.test(t)) throw new Error(t);
      await p().waitForTimeout(250);
    }
    throw new Error(`No status matching ${re}`);
  };
  /** The bank's configuration set on the tenant page: changed, pasted into the editor, checked as typed, saved as a draft. */
  const configuration = async (change: (c: any) => void) => {
    await go('tenant');
    const cs = (await d.api(MAKER).get(`/api/tenants/${BANK}/configuration`)).data;
    const next = JSON.parse(JSON.stringify(cs.configurationSet));
    next.release = bump(next.release);
    change(next);
    const box = p().getByLabel('Configuration set YAML');
    for (let i = 0; i < 120 && !(await box.inputValue()).includes(String(cs.configurationSet.release)); i++) await p().waitForTimeout(100);
    await box.scrollIntoViewIfNeeded();
    await d.paste(box, toYaml(next));
    const save = p().getByRole('button', { name: 'Save the configuration set' });
    await d.skip('The checks run', async () => { for (let i = 0; i < 300 && !(await save.isEnabled()); i++) await p().waitForTimeout(100); }, 8);
    await d.wait(1200);
    await d.click(save);
    await status(/^Saved /);
    await d.wait(1200);
    return next;
  };
  /** The bank approver approves the configurator's change set in Governance, as a version. */
  const approveConfiguration = async () => {
    await studioAs(CHECKER);
    await go('governance');
    const row = p().locator('tr', { hasText: `config:${BANK}` }).first();
    await d.point(row, 1500);
    await d.click(row.getByRole('button', { name: 'Approve as version' }));
    await d.skip('The approval builds a configuration release', async () => status(/approved as a version/), 8);
    await d.wait(1500);
  };
  /** The bank approver's configuration release with the laptop's bindings, built by the studio's API (no screen builds one with bindings). */
  const laptopRelease = async (suffix: string) => {
    const set = (await d.api(CHECKER).get(`/api/tenants/${BANK}/configuration`)).data.configurationSet;
    const r = await d.api(CHECKER).post('/api/configuration-releases', { actor: CHECKER, tenant: BANK, bank: BANK, release: `${set.release}+bindings.${suffix}`, bindings: laptopBindings() });
    if (r.status !== 200) throw new Error(`configuration release: ${JSON.stringify(r.data)}`);
    await term(`curl -s -X POST http://localhost:4317/api/configuration-releases -d '{"bank":"${BANK}","release":"${set.release}+bindings.${suffix}","bindings": <deploy/laptop/banks/${BANK}/*.yaml>}'   # as ${CHECKER}`,
      JSON.stringify({ releaseId: r.data.releaseId, tag: r.data.tag, files: r.data.files, bindings: laptopBindings().map((b: any) => `${b.port} -> ${b.adapter}`) }, null, 2));
    return r.data as { tag: string; releaseId: string };
  };

  // ------------------------------------------------------------ the runtime's consoles
  const seen: Record<string, string> = {};
  const fresh = async (base: string, hash: string) => {
    if (p().url() === `${base}/#/${hash}`) await p().goto(`${base}/`);
    await runtimeGo(d, base, hash);
  };
  const svcAs = async (who: 'service engineer' | 'bank approver', page: string) => {
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
  /** The health page's circuit breakers row, and its detail cell (the row is wider than the page: pointing at it scrolls sideways). */
  const breakerRow = () => p().locator('[data-testid="health-row"][data-name="circuit-breakers"]');
  const breakerDetail = () => breakerRow().locator('td').nth(3);
  const versionRow = (tag: string) => p().locator(`[data-testid="version-row"][data-tag="${tag}"]`);
  const releaseRow = (id: string) => p().locator(`[data-testid="release-row"][data-release="${id}"]`);
  const versions = async () => ((await call(SVC, 'svc.engineer', 'GET', '/content/versions')).data as any[]).filter((v) => v.bank === BANK);
  const install = async (tag: string, reason: string) => {
    await svcAs('service engineer', 'content');
    await d.point(versionRow(tag), 1200);
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
  const pinOf = async (paymentId: string) => (await call(URLS.release, 'th-pipeline', 'GET', `/payments/pin?paymentId=${encodeURIComponent(paymentId)}`)).data;
  const ended = async (paymentId: string) => (await pinOf(paymentId))?.ended as string | undefined;

  // ------------------------------------------------------------ files: the File API, the SFTP server
  /** A salary file the bank's channel sends to the File API (POST /files), shown as the command an integrator would run. */
  const send = async (f: { name: string; text: string }) => {
    const r = await call(URLS.files, 'th-channel', 'POST', '/files', { tenant: BANK, message: 'uae.wps.sif', fileName: f.name, contentBase64: Buffer.from(f.text).toString('base64') });
    if (r.status !== 202 || r.data?.disposition !== 'started') throw new Error(`POST /files: ${r.status} ${JSON.stringify(r.data)}`);
    await term(`curl -s -X POST http://localhost:8485/files -H "Authorization: Bearer $CHANNEL_TOKEN" -d '{"tenant":"${BANK}","message":"uae.wps.sif","fileName":"${f.name}","contentBase64":"..."}'`,
      `${f.text}\n${JSON.stringify(r.data, null, 2)}`);
    return r.data.payment as string;
  };
  const ls = (dir: string) => sh(`${C} exec -T sftp sh -c 'ls -1 ${dir} 2>/dev/null || true'`).split('\n').map((l) => l.trim()).filter(Boolean);
  const sifOn = async (stem: string, ms = 5 * 60_000) => {
    await until(`the SIF ${stem} on the SFTP server`, async () => ls(CB_SIF).some((n) => n.toUpperCase() === `${stem}.SIF`), ms);
    return ls(CB_SIF).find((n) => n.toUpperCase() === `${stem}.SIF`)!;
  };
  const showSif = async (sif: string) => {
    await term(`sftp -P 12222 th@127.0.0.1   # ls upload/cb/wps/sif`, `${sh(`${C} exec -T sftp ls -l ${CB_SIF}`)}\n\n$ head -c 200 upload/cb/wps/sif/${sif}\n${sh(`${C} exec -T sftp head -c 200 ${CB_SIF}/${sif}`)}`);
  };
  let answers = 0;
  /** The Central Bank's answer, left in its ACK folder on the SFTP server: an ACK, or a NAK with one line's error. */
  const answer = async (sif: string, accepted: boolean) => {
    const stem = sif.replace(/\.SIF$/i, '');
    const name = `${stem}S26${String(++answers).padStart(9, '0')}.${accepted ? 'ACK' : 'NAK'}`;
    const text = accepted ? `AHR,ACCEPTED,${sif}\nATR,ACCEPTED,2\n` : `AHR,REJECTED,${sif}\nDER,1,E0042,The salary of line 1 does not match the control record (synthetic)\nATR,REJECTED,3\n`;
    const file = path.join(work, name);
    fs.writeFileSync(file, text);
    const out = sh(`${C} exec -T -u 1001 sftp sh -c 'mkdir -p ${CB_ACK} ${SFTP_HOME}/cb/wps/writing && cat > ${SFTP_HOME}/cb/wps/writing/${name} && mv ${SFTP_HOME}/cb/wps/writing/${name} ${CB_ACK}/${name} && ls -l ${CB_ACK}' < ${file}`);
    await term(`sftp -P 12222 th@127.0.0.1   # put ${name} upload/cb/wps/ack/`, `${text}\n$ ls -l upload/cb/wps/ack\n${out}`);
  };

  // ------------------------------------------------------------ a payment's page in the operations console
  const rowOf = (f: { stem: string }) => p().locator('[data-testid="payment-row"]', { hasText: f.stem }).first();
  const openPayment = async (paymentId: string, f: { stem: string }) => {
    await opsAs('operator', 'payments');
    const row = rowOf(f);
    for (let i = 0; i < 60 && !(await row.count()); i++) { await p().waitForTimeout(1000); await fresh(URLS.ops, 'payments'); }
    if (await row.count()) {
      await d.point(row, 1200);
      await d.click(row.locator('a').first());
    } else await fresh(URLS.ops, `payments/${encodeURIComponent(paymentId)}`);
    await p().locator('main h1').first().waitFor({ timeout: 15_000 });
    await d.wait(1000);
  };
  const tab = async (name: RegExp) => {
    const t = p().getByRole('tab', { name });
    await t.first().waitFor({ timeout: 15_000 });
    await d.click(t.first());
    await d.wait(800);
  };
  /** A field of the payment's page, by its label: the row that holds it, pointed at. */
  const field = (label: string) => main().getByText(label, { exact: true }).first().locator('xpath=..');

  // ------------------------------------------------------------ the studio's debugger
  const dbgBar = () => p().getByRole('toolbar', { name: 'Debugger controls' });
  const dbgNow = () => p().getByTestId('dbg-now');
  const stepUntil = async (button: string, re: RegExp, max = 40) => {
    for (let i = 0; i < max && !re.test((await dbgNow().textContent()) ?? ''); i++) {
      await d.click(dbgBar().getByRole('button', { name: button }));
      await d.wait(500);
    }
    if (!re.test((await dbgNow().textContent()) ?? '')) throw new Error(`The debugger does not reach ${re}: ${await dbgNow().textContent()}`);
    await d.point(dbgNow(), 1500);
  };

  const state: { P1?: string; f1?: any; sif1?: string; P2?: string; f2?: any; sif2?: string; P3?: string; f3?: any; P4?: string; f4?: any; sif4?: string;
    P5?: string; f5?: any; sif5?: string; P6?: string; f6?: any; sif6?: string; R2?: any; B?: any; R3?: any; cut?: string } = {};
  const QUICK_BENCH = d.dry && process.env.TH_DEMO_QUICK_BENCH === '1';

  // A dry run may start at a later scene while the script is worked on (TH_DEMO_FROM): each chapter from 3 on sends files of its own.
  const all: Scene[] = [
    // ================================================================== 0
    {
      card: ['A salary file, end to end', 'On the laptop stack: the test bench, each step, two failures, a rule change and a new hook'],
      say: 'In this video we follow one salary file all the way through TransactHub, slowly. We send it from the test bench, and watch every step of the workflow run. Then two things go wrong: first a business failure, then a technical one, and we see how the consoles show each, and how staff put it right. Last, the bank changes a business rule, and adds a step of its own, and we run the same file again to see the difference. Everything is synthetic: no real customer, no real account.',
    },
    // ================================================================== 1. the test bench
    {
      card: ['1. The test bench sends a file', 'A WPS salary file, on Temporal, beside the reference interpreter'],
      say: 'We start in the test bench, as the bank\'s tester. The tester picks a profile: a salary file on the default engine, Temporal. The bank is A D I B. The business date is today. The bench lets the tester change it, for a file that must count for another day.',
      do: async () => {
        await runtimeSignIn(d, URLS.testbench, 'tester', 'testbench');
        await d.select(p().getByTestId('profile'), 'wps-default-engine');
        await d.wait(800);
        await d.select(p().locator('fieldset', { hasText: 'Tenant' }).locator('select'), BANK);
        const date = p().getByTestId('business-date');
        await date.scrollIntoViewIfNeeded();
        await d.point(date, 1500);
        await date.fill(new Date().toISOString().slice(0, 10));
      },
    },
    {
      say: 'The data is a synthetic salary file from the bench\'s library: one employer, one employee, one thousand five hundred dirhams. The bench checks each engine\'s run against the studio\'s own interpreter, step by step.',
      do: async () => {
        const sample = p().getByTestId('sample');
        const value = await sample.locator('option').evaluateAll((os: any[]) => os.find((o) => /wps-sif-happy/.test(o.value))?.value ?? os.find((o) => /wps/i.test(o.value) && /expect settled/i.test(o.textContent))?.value);
        if (!value) throw new Error('The library has no WPS sample');
        await d.select(sample, value);
        if (QUICK_BENCH) { await p().getByTestId('engine-temporal').uncheck(); await p().getByTestId('engine-dtfx').check(); }
        await d.point(p().getByTestId('run'), 1000);
      },
    },
    {
      say: 'Run. The bench exports the bank\'s package, starts its workers, and runs the file on Temporal, with its own database. A real Temporal server runs in real time, and the Central Bank\'s simulator answers after ten minutes of business time. So we skip ahead.',
      do: async () => {
        await d.click(p().getByTestId('run'));
        const st = p().getByTestId('status');
        await st.waitFor({ timeout: 60_000 });
        await d.wait(3000);
        await d.skip('Temporal runs the file in real time', async () => {
          for (let i = 0; i < 1500 && !/passed|failed/.test((await st.textContent()) ?? ''); i++) await p().waitForTimeout(1000);
        }, 6);
        const t = (await st.textContent()) ?? '';
        if (!/passed/.test(t)) throw new Error(`The bench run ${t}: ${(await p().getByTestId('result').textContent())?.slice(0, 1500)}`);
        await d.point(st, 1500);
      },
    },
    {
      say: 'Passed. Temporal and the interpreter took the same steps, to the same end: settled. Here is the trace, one line per step: the checks, screening, the funds reserved, the rail chosen, the W P S leg, the posting, and the notices.',
      do: async () => {
        const engine = QUICK_BENCH ? 'dtfx' : 'temporal';
        await d.point(p().getByTestId(`row-${engine}`), 2000);
        const trace = p().getByTestId(`trace-${engine}`);
        await trace.scrollIntoViewIfNeeded();
        await d.point(trace, 4000);
      },
    },
    {
      say: 'The bench proves the workflow. Now the real thing. The bank\'s channel sends a salary file to the runtime\'s File API, the one door for every file. The File API keeps the file with its hash, checks it is new, and starts its payment on the bank\'s active distribution.',
      do: async () => {
        state.f1 = salaryFile();
        state.P1 = await send(state.f1);
      },
    },
    // ================================================================== 2. step by step
    {
      card: ['2. Each step of the workflow', 'In the studio\'s debugger, then on the payment\'s timeline'],
      say: 'Before we look at that payment, let us watch each step of the workflow run, one at a time, in the studio\'s debugger. We sign in to the studio as the bank\'s configurator, and start the bench from the bank\'s own end to end test: the corporate workflow, today\'s date, a salary file, and a simulator for every partner.',
      do: async () => {
        await close();
        await studioAs(MAKER);
        await go('tests');
        await d.click(p().getByRole('tab', { name: 'Ad hoc bench' }));
        await d.select(p().getByLabel('Load from test'), 'BANK-WPS-001');
        await d.wait(1500);
        // The test's start message: the salary file comes from the bank's channel, through the front door.
        await d.select(p().getByLabel('Start message'), 'uae.wps.sif');
        await d.point(main().getByText('Ports and simulators').first().locator('xpath=..'), 2500);
      },
    },
    {
      say: 'Debug step by step. On the left, the workflow and its steps. On the right, what just happened, and the data as it stands. Step into. First the front door: the intake map reads the salary file into the ISO 20022 model, one payment with one line.',
      do: async () => {
        await main().getByText('Input').first().scrollIntoViewIfNeeded().catch(() => undefined);
        await d.click(p().getByRole('button', { name: 'Debug step by step' }));
        await dbgBar().waitFor({ timeout: 60_000 });
        await dbgBar().scrollIntoViewIfNeeded();
        await d.point(main().getByLabel('Workflow steps'), 1500);
        await stepUntil('Step into ▶', /sif-in|intake/i, 4);
        await d.point(p().getByTestId('dbg-data'), 1500);
      },
    },
    {
      say: 'Then the platform workflow, the same for every scheme. Validate checks the payment is well formed. The duplicate check makes sure this file was not paid before.',
      do: async () => { await stepUntil('Step over ⤼', /duplicate/); },
    },
    {
      say: 'Screening runs two checks side by side: sanctions, and fraud. Both clear. Then the limits, and the funds are reserved on the employer\'s account.',
      do: async () => { await stepUntil('Step over ⤼', /reserve/); },
    },
    {
      say: 'Route chooses the rail. For a salary file, that is W P S, the Central Bank\'s wage protection system, enabled for this bank.',
      do: async () => { await stepUntil('Step over ⤼', /route/); await d.point(p().getByTestId('dbg-decisions'), 1500); },
    },
    {
      say: 'Now the W P S leg, in the scheme slot. We step into it. First the business date, and the bank\'s cut-off: the file arrived before the cut-off, so it counts for today.',
      do: async () => {
        await stepUntil('Step into ▶', /cut-off/i, 30);
        await d.point(p().getByTestId('dbg-changed'), 1500);
      },
    },
    {
      say: 'Then the scheme\'s own checks: the employer is registered, the file\'s control record matches its lines, and each line\'s account is a valid I BAN.',
      do: async () => { await stepUntil('Step over ⤼', /iban|record-checks|file-checks/i, 20); },
    },
    {
      say: 'The leg renders the salary information file, the S I F, in the Central Bank\'s format, encrypts it for W P S, and sends it.',
      do: async () => { await stepUntil('Step over ⤼', /send|submit/i, 20); },
    },
    {
      say: 'It then waits for the Central Bank\'s acknowledgement, matched to this file. The simulator answers with an ACK: accepted. Continue runs to the end. The payment is posted, the posting verified, the channel told, and it settles.',
      do: async () => {
        await d.click(dbgBar().getByRole('button', { name: 'Continue ⏭' }));
        await d.wait(1500);
        await expectText(/settled/i, main());
        await d.point(dbgNow(), 2000);
      },
    },
    {
      say: 'That was the studio, on its own clock, with simulators. Now the real payment we sent through the File API, in the operations console, signed in as an operator.',
      do: async () => { await openPayment(state.P1!, state.f1); },
    },
    {
      say: 'Its timeline is a list of facts, one per step, each chained to the one before by its hash. The same steps we just watched: intake, the checks, screening, the reservation, the route, the business date and cut-off, the S I F rendered, encrypted, and handed to the File API.',
      do: async () => {
        await tab(/^Timeline/);
        await d.scroll(300);
        await d.wait(2500);
      },
    },
    {
      say: 'The File API delivers the S I F through its outbox, to the Central Bank\'s folder on the SFTP server. Here it is. The payment now waits for the Central Bank\'s answer.',
      do: async () => {
        state.sif1 = await sifOn(state.f1.stem);
        await showSif(state.sif1);
      },
    },
    {
      say: 'We play the Central Bank. It leaves an ACK in its answer folder, named after the S I F. The File API picks it up, matches it to the payment by the file\'s name, and hands it to the payment\'s run.',
      do: async () => {
        await answer(state.sif1!, true);
        await d.skip('The payment reads the ACK and runs to its end', async () => until('P1 ended', async () => !!(await ended(state.P1!))), 6);
        if ((await ended(state.P1!)) !== 'settled') throw new Error(`P1 ended ${await ended(state.P1!)}`);
      },
    },
    {
      say: 'Settled. The leg shows the Central Bank\'s response: ACK, and when it came. And the timeline has the last steps: the ACK read, the posting, its verification, and the notices, including the bank\'s own labour ministry notice, a hook, when the payment closed.',
      do: async () => {
        await close();
        await openPayment(state.P1!, state.f1);
        await tab(/^Legs/);
        await d.point(main().getByText('CB response (ACK or NAK)').first().locator('xpath=..'), 2000);
        await tab(/^Timeline/);
        await d.scroll(900);
        await d.wait(2500);
      },
    },
    // ================================================================== 3. a business failure
    {
      card: ['3. A business failure', 'The Central Bank refuses a file'],
      say: 'Now a business failure. The bank\'s channel sends another salary file. It passes every check of ours, and its S I F goes to the Central Bank, as before.',
      do: async () => {
        state.f2 = salaryFile();
        state.P2 = await send(state.f2);
        state.sif2 = await sifOn(state.f2.stem);
      },
    },
    {
      say: 'This time the Central Bank refuses it, with a NAK. The NAK names the line and the error: the salary of line one does not match the control record.',
      do: async () => {
        await answer(state.sif2!, false);
        await d.skip('The payment reads the NAK', async () => until('P2 ended', async () => !!(await ended(state.P2!))), 6);
        if ((await ended(state.P2!)) === 'settled') throw new Error('P2 settled on a NAK');
      },
    },
    {
      say: 'In the operations console, the payments list shows it at once: status R J C T, rejected, beside the settled one. The operator can filter by status, or by the leg\'s outcome, to find every refused file.',
      do: async () => {
        await close();
        await opsAs('operator', 'payments');
        const row = rowOf(state.f2);
        await row.waitFor({ timeout: 60_000 });
        await row.scrollIntoViewIfNeeded();
        await d.point(row, 2500);
      },
    },
    {
      say: 'On the payment\'s page: the status, and the Central Bank\'s response, NAK, with the reason, and the error for each line. Nothing was posted: the funds reserved for it were released, and the channel was told.',
      do: async () => {
        await openPayment(state.P2!, state.f2);
        await tab(/^Legs/);
        await d.point(main().getByText('CB response (ACK or NAK)').first().locator('xpath=..'), 1800);
        await d.point(main().getByText('NAK errors per line').first().locator('xpath=..'), 2000);
        await tab(/^Timeline/);
        await d.scroll(900);
        await d.wait(1500);
      },
    },
    {
      say: 'A business failure is put right by the business. The employer corrects the file, and the bank sends it again, as a new file with its own reference. It takes the same path, and this time the Central Bank accepts it.',
      do: async () => {
        state.f3 = salaryFile();
        state.P3 = await send(state.f3);
        const sif = await sifOn(state.f3.stem);
        await answer(sif, true);
        await d.skip('The corrected file runs to its end', async () => until('P3 ended', async () => !!(await ended(state.P3!))), 6);
        if ((await ended(state.P3!)) !== 'settled') throw new Error(`P3 ended ${await ended(state.P3!)}`);
        await close();
        await opsAs('operator', 'payments');
        await d.point(rowOf(state.f3), 2500);
      },
    },
    // ================================================================== 4. a technical failure
    {
      card: ['4. A technical failure', 'The SFTP partner is down'],
      say: 'Now a technical failure, which no business person can fix. The Central Bank\'s SFTP server goes down. On this laptop, we stop its container.',
      do: async () => { await term(`${C} stop sftp`, sh(`${C} stop sftp`)); },
    },
    {
      say: 'A salary file arrives. Its checks pass, and its S I F is rendered and handed to the File API. But the File API cannot reach the SFTP server. It tries again, a few times. After five failures in a row, the circuit breaker for that partner opens: the File API stops calling it for a while, so nothing piles up against a server that is down.',
      do: async () => {
        state.f4 = salaryFile();
        state.P4 = await send(state.f4);
        await until('the breaker open', async () => {
          const h = (await call(SVC, 'svc.engineer', 'GET', '/health?refresh=1')).data;
          return (h.components ?? []).some((c: any) => c.name === 'circuit-breakers' && c.status === 'fail' && /file-transfer/.test(c.detail ?? ''));
        }, 5 * 60_000);
      },
    },
    {
      say: 'The service engineer sees it in the service console. Health is red. The circuit breakers row says which: the file transfer port, the S F T P adapter, its host, open since when, after how many failures.',
      do: async () => {
        await close();
        await svcAs('service engineer', 'health');
        await breakerRow().waitFor({ timeout: 30_000 });
        await d.point(breakerDetail(), 3500);
      },
    },
    {
      say: 'Every error the console shows links to its entry in the error dictionary. The entry says what the code means, whether a retry can help, and the runbook: what to check, and what to do.',
      do: async () => {
        const link = breakerRow().getByTestId('error-link').first();
        await d.click(link);
        const rb = p().getByTestId('runbook');
        await rb.waitFor({ timeout: 15_000 });
        await d.point(rb, 4000);
      },
    },
    {
      say: 'The engineer brings the partner back: on this laptop, we start the container again. When the breaker\'s wait is over, it lets one call through. It succeeds, and the breaker closes: its row is green again. The disk row stays red, for a reason of its own: this laptop\'s disk is nearly full, and the console says so the same way.',
      do: async () => {
        await term(`${C} start sftp`, sh(`${C} start sftp`));
        await d.skip('The breaker tries again and closes', async () => until('the breaker closed', async () => {
          const h = (await call(SVC, 'svc.engineer', 'GET', '/health?refresh=1')).data;
          return (h.components ?? []).some((c: any) => c.name === 'circuit-breakers' && !/file-transfer/.test(c.detail ?? ''));
        }, 5 * 60_000), 6);
        await close();
        await svcAs('service engineer', 'health');
        await d.point(breakerDetail(), 2500);
      },
    },
    {
      say: 'But the S I F of our payment is not there: the File API gave up delivering it while the partner was down, and the payment still waits for an answer. The engineer resends it. Resend file puts the same bytes the File API kept on the port again. It is never rendered or signed again.',
      do: async () => {
        // The outbox's delivery attempts were used up while the partner was down: the SIF is not on the SFTP server.
        if (ls(CB_SIF).some((n) => n.toUpperCase() === `${state.f4.stem}.SIF`)) throw new Error('The SIF of P4 reached the SFTP server without a resend');
        await svcAs('service engineer', 'actions');
        await d.select(p().locator('select[name="action"]'), 'resend-file');
        await d.type(p().locator('input[name="target"]'), state.P4!, 15);
        await d.point(p().getByTestId('action-spec'), 1500);
        await d.click(p().getByRole('button', { name: 'Ask for it' }));
        await d.type(p().getByRole('dialog').getByLabel(/Why/), 'INC-E2E-004: the SFTP partner was down; send the kept SIF again (synthetic)', 20);
        await d.click(p().getByRole('dialog').getByRole('button', { name: /^Confirm/ }));
        await p().locator('[data-testid="result"], [role="alert"]').first().waitFor({ timeout: 60_000 });
        await d.wait(1200);
      },
    },
    {
      say: 'A resend changes what leaves the bank, so a bank approver must consent. Once they do, the File API sends the file, and here it is on the SFTP server.',
      do: async () => {
        await consent('Agreed: INC-E2E-004 (synthetic)');
        state.sif4 = await sifOn(state.f4.stem, 2 * 60_000);
        await showSif(state.sif4);
      },
    },
    {
      say: 'The Central Bank answers with an ACK, and the payment goes on to the end. The technical failure cost a delay, and nothing else: no payment was lost, and none was sent twice.',
      do: async () => {
        await answer(state.sif4!, true);
        await d.skip('The payment reads the ACK and runs to its end', async () => until('P4 ended', async () => !!(await ended(state.P4!))), 6);
        if ((await ended(state.P4!)) !== 'settled') throw new Error(`P4 ended ${await ended(state.P4!)}`);
        await close();
        await openPayment(state.P4!, state.f4);
        await tab(/^Timeline/);
        await d.scroll(900);
        await d.wait(2000);
      },
    },
    {
      say: 'The service console\'s log tells the same story for the engineer. Filtered by the payment, it lists each fact, and who did it: the engineer\'s request to resend, the approver\'s consent, the file sent again, the ACK, and the end. Each line links to the payment, and to its trace.',
      do: async () => {
        await close();
        await svcAs('service engineer', 'logs');
        await d.type(p().locator('input[name="payment"]'), state.P4!, 15);
        await d.click(p().getByRole('button', { name: 'Filter' }));
        const row = p().locator('[data-testid="log-row"]', { hasText: 'action.requested' }).first();
        await row.waitFor({ timeout: 30_000 });
        await d.point(p().locator('[data-testid="log-row"]').first().locator('td').nth(3), 1500);
        await d.point(row.locator('td').nth(3), 2500);
      },
    },
    // ================================================================== 5. a business-rule change
    {
      card: ['5. A business-rule change', 'A dial changed in the studio, approved, and put in force'],
      say: 'Now the bank changes a business rule. Its cut-off for salary files says until what time a file counts for today, and what happens to a file after it. We sign in to the studio as the bank\'s configurator, and open the bank\'s configuration.',
      do: async () => {
        await close();
        await studioAs(MAKER);
        await go('tenant');
        await expectText(/Parameters/);
        const row = main().getByText(CUT_OFF.split('#')[1], { exact: false }).first();
        await d.point(row.locator('xpath=ancestor::tr[1]'), 2500);
      },
    },
    {
      say: 'Today the cut-off is half past eleven at night, Dubai time. The configurator moves it to an hour ago, and keeps the rule for a late file: it counts for the next business day. The checks run as we type; every value must stay inside the bounds the platform set.',
      do: async () => {
        state.cut = dubai(-60).replace(/:\d\d$/, ':00');
        await configuration((c) => {
          c.values = c.values.filter((v: any) => v.parameter !== CUT_OFF);
          c.values.push({ parameter: CUT_OFF, value: cutOff(state.cut!), validFrom: '2026-11-24' });
        });
      },
    },
    {
      say: 'The configurator cannot put a change in force alone. The bank approver, another person, signs in and approves it as a version. That builds a configuration release: values only, never code.',
      do: async () => { await approveConfiguration(); },
    },
    {
      say: 'On this laptop, every partner of the bank is the stack\'s own SFTP server, so the approver builds the release with the laptop\'s bindings, by the studio\'s API.',
      do: async () => { state.R2 = await laptopRelease('1.0.1'); },
    },
    {
      say: 'In the service console, the engineer installs the release, and the bank approver consents. The runtime checks every value against the bounds the distribution declares, and puts it in force. Nothing is redeployed for a value.',
      do: async () => {
        await close();
        await install(state.R2.tag, `CR-E2E-005: put ${state.R2.releaseId} in force (synthetic)`);
        await d.point(releaseRow(state.R2.releaseId), 2500);
      },
    },
    {
      say: 'The same salary file again, with a new file reference. It arrives after the new cut-off.',
      do: async () => {
        state.f5 = salaryFile();
        state.P5 = await send(state.f5);
        state.sif5 = await sifOn(state.f5.stem);
        await answer(state.sif5, true);
        await d.skip('The payment runs to its end', async () => until('P5 ended', async () => !!(await ended(state.P5!))), 6);
      },
    },
    {
      say: 'On its page, the leg shows the difference. The cut-off was applied: the file came after it, so it counts for the next business day, tomorrow, not today. Same file, same workflow, a different outcome, from one value the bank changed and approved.',
      do: async () => {
        await close();
        await openPayment(state.P5!, state.f5);
        await tab(/^Legs/);
        await d.point(main().getByText('Cut-off time', { exact: true }).first().locator('xpath=..'), 1500);
        await d.point(main().getByText('After the cut-off', { exact: true }).first().locator('xpath=..'), 1500);
        await d.point(main().getByText('Counts for business date', { exact: true }).first().locator('xpath=..'), 2500);
      },
    },
    // ================================================================== 6. a new hook
    {
      card: ['6. A new hook', 'The bank adds a check of its own, at a hook point'],
      say: 'Last, the bank adds a step of its own. The shared workflow leaves hook points: places where a bank may plug in its own logic, without changing the recipe. One sits right after validation. It is not bound for this bank.',
      do: async () => {
        await studioAs(MAKER);
        await go('tenant');
        await expectText(/Hook points/);
        const row = main().getByText('after-validate', { exact: false }).first().locator('xpath=ancestor::tr[1]');
        await row.scrollIntoViewIfNeeded();
        await d.point(row, 3000);
      },
    },
    {
      say: 'The bank already has a hook for it: an employer A M L check, which screens the employer of a salary file with the bank\'s own screening service. The configurator binds it to the hook point, from today. A binding is configuration too.',
      do: async () => {
        await configuration((c) => {
          c.bindings = (c.bindings ?? []).filter((b: any) => b.hookPoint !== AML_POINT);
          c.bindings.push({ hookPoint: AML_POINT, hook: AML_HOOK, release: '1.0.0', validFrom: '2026-11-24' });
        });
      },
    },
    {
      say: 'The bank approver approves it. A hook is logic, so it travels in the bank\'s distribution: the approver builds the bank\'s next distribution, B, signed, with the hook in it.',
      do: async () => {
        await approveConfiguration();
        const r = await d.api(CHECKER).post('/api/distributions', { actor: CHECKER, tenant: BANK, bank: BANK, engine: 'temporal', withSimulators: true });
        if (r.status !== 200) throw new Error(`distribution B: ${JSON.stringify(r.data).slice(0, 800)}`);
        state.B = r.data;
        await term(`curl -s -X POST http://localhost:4317/api/distributions -d '{"bank":"${BANK}","engine":"temporal"}'   # as ${CHECKER}`,
          JSON.stringify({ tag: r.data.tag, release: r.data.release ?? r.data.id, hooks: [...new Set((JSON.stringify(r.data).match(/b\.hook\.[a-z0-9.-]+@[0-9.]+/g) ?? []))] }, null, 2));
      },
    },
    {
      say: 'In the service console, B is offered. The engineer installs it, and activates it, and the approver consents to each. New payments now take B. Then the configuration release with the binding is put in force, the same way.',
      do: async () => {
        await close();
        const B = (await versions()).find((v) => v.kind === 'distribution' && v.state === 'offered');
        if (!B) throw new Error('distribution B is not offered');
        state.B = B;
        await install(B.tag, `CR-E2E-006: install ${B.release} (synthetic)`);
        await activate(B.releaseId, `CR-E2E-006: activate ${B.release} (synthetic)`);
        state.R3 = await laptopRelease('1.0.2');
        await close();
        await install(state.R3.tag, `CR-E2E-007: put ${state.R3.releaseId} in force (synthetic)`);
        await d.point(p().getByRole('table', { name: 'Releases' }), 2500);
      },
    },
    {
      say: 'The same file once more, with a new reference. It is pinned to B when it is accepted.',
      do: async () => {
        await close();
        state.f6 = salaryFile();
        state.P6 = await send(state.f6);
        state.sif6 = await sifOn(state.f6.stem);
        await answer(state.sif6, true);
        await d.skip('The payment runs to its end', async () => until('P6 ended', async () => !!(await ended(state.P6!))), 6);
        if ((await ended(state.P6!)) !== 'settled') throw new Error(`P6 ended ${await ended(state.P6!)}`);
      },
    },
    {
      say: 'On its timeline, right after the validation checks, there is a new step: the bank\'s employer A M L check ran, as the hook, and the employer is clear. Then the payment went on, exactly as before, and settled.',
      do: async () => {
        await close();
        await openPayment(state.P6!, state.f6);
        await tab(/^Timeline/);
        const hook = main().getByText(/employer-aml|b\.screen\.employer-aml/).first();
        await hook.waitFor({ timeout: 30_000 });
        await hook.scrollIntoViewIfNeeded();
        await d.point(hook.locator('xpath=ancestor::li[1]'), 3500);
      },
    },
    {
      card: ['That is the whole file, end to end', 'Sent, run step by step, refused and corrected, stalled and resent, changed by a value, and extended by a hook.'],
      say: 'That is the whole journey. A salary file sent from the test bench and through the File API, and every step of its workflow. A business failure, shown with its reason and put right by a corrected file. A technical failure, shown by the service console with its runbook, and put right by a resend, with a second person\'s consent. A business rule changed by the bank and approved, and its effect on the next file. And a hook of the bank\'s own, added as configuration, running in the timeline.',
    },
  ];
  return d.dry && process.env.TH_DEMO_FROM ? all.slice(Number(process.env.TH_DEMO_FROM)) : all;
}
