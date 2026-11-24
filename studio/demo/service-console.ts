import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { parseYaml, toYaml } from '../src/core/yaml.js';
import { NORMAL, SLOW, type Director, type Scene } from './record.js';
import { BANK, LAPTOP, ROOT, URLS, call, mask, runtimeGo, runtimeSignIn, sh, stackUp, useStackStudio } from './stack.js';

// Video 5c, the service console, on the laptop stack (deploy/laptop), for one bank (ADIB by default), page by page:
//
//   1. Health: every probe (the store, the read model, the package source, the trusted signers, Temporal, the disk, the circuit
//      breakers), their history, and the laptop's own real problem, its disk nearly full, with its runbook.
//   2. Content: the versions offered on GitHub and their states; Verify and the Difference; a new configuration release installed
//      and put in force with consent, the earlier ones superseded; the values in force; a roll back to distribution A and B
//      activated again; the payments pinned but never started, and end-pin.
//   3. Consents: asked by the service engineer, given or refused by the bank approver; the standing consent to rate releases.
//   4. A problem diagnosed from the console: the native host is down, a salary file's render step parks, the log names the error,
//      the engineer brings the host back and retries the parked step with the approver's consent, and the payment goes on.
//   5. Resend a kept file, after the Central Bank's folder lost it; requeue and re-drive in the catalogue.
//   6. The log and the audit chain; the error dictionary and its runbooks; backup, verify and dry run; the trusted signers and
//      each distribution's native modules by their SHA-256, re-verified; the assistant, off until the bank's data protection
//      officer approves it.
//
// The stack must be up with its secrets in the environment (deploy/laptop/README.md). The set-up before the camera, cut from the
// video, brings it to the end state of video 4 by the APIs when it is not there (endOfVideo4), and builds the bank's next
// configuration release in the studio, with the laptop's bindings.

export const NAME = '5c-service-console';
export const TITLE = 'TransactHub: the service console, page by page';
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
/** The laptop's bindings of the bank's file transfer (deploy/laptop/banks/<bank>), as video 4 builds them in. */
const laptopBindings = () => ['file-transfer.yaml'].map((f) => path.join(LAPTOP, 'banks', BANK, f)).filter((f) => fs.existsSync(f))
  .flatMap((f) => parseYaml(fs.readFileSync(f, 'utf8')).bindings);
const cutOff = (at: string) => [{ scheme: 'uae.wps', direction: 'outward', cutOff: at, timeZone: 'Asia/Dubai', afterCutOff: 'next-business-day' }];
const bump = (r: string) => String(r).replace(/^(\d+)\.(\d+)\..*/, (_: string, M: string, m: string) => `${M}.${Number(m) + 1}.0`);
const versions = async () => ((await call(SVC, 'svc.engineer', 'GET', '/content/versions')).data as any[]).filter((v) => v.bank === BANK);
const releases = async () => ((await call(SVC, 'svc.engineer', 'GET', '/content/releases')).data as any[]);

let seq = 0;
/** A synthetic salary file of the bank's employer, with a file reference of its own (the File API refuses a file it took before). */
function salaryFile(): { name: string; stem: string; text: string } {
  const sample = fs.readFileSync(path.join(ROOT, 'studio', 'fixtures', 'samples', 'wps-sif-no-file-line.sif'), 'utf8');
  const t = new Date();
  const stamp = `${String(t.getUTCHours()).padStart(2, '0')}${String(t.getUTCMinutes()).padStart(2, '0')}`;
  const n = String(50 + ++seq).padStart(2, '0');
  const stem = `0000001234567261117${stamp}${n}`;
  return { name: `${stem}.sif`, stem, text: sample.replace(',0830,', `,${stamp},`).replace('REF-0001', `REF-${stamp}${n}`) };
}

/** What the set-up leaves for the scenes: the configuration release to install, and the distributions. */
const setup: { next?: { tag: string; releaseId: string }; A?: any; B?: any } = {};

/** An action asked by the engineer and consented to by the approver, by the service API (set-up only, off camera). */
async function consented(action: string, target: string, why: string) {
  const ask = await call(SVC, 'svc.engineer', 'POST', '/actions', { action, target, reason: why });
  if (ask.data?.status !== 'pending-approval') return ask.data;
  return (await call(SVC, 'bank.approver', 'POST', `/actions/${encodeURIComponent(ask.data.action)}/decision`, { approve: true, reason: 'Agreed: set-up before the video (synthetic)' })).data;
}

/**
 * The end state of video 4, built by the studio's and the service console's APIs on a stack that does not have it yet (a fresh
 * stack, or one whose studio restarted and lost its in-memory GitHub): the bank's two studio users; distribution A installed and
 * activated; its configuration release with the laptop's file-transfer binding and the cut-off at 23:30 in force; the employer AML
 * hook bound after validation, distribution B built, installed and activated, and its configuration release in force; and one
 * salary file on B waiting for the Central Bank's answer, so that B has an open payment. Each step with a bank approver's consent.
 */
async function endOfVideo4(d: Director) {
  const admin = d.api('users.admin');
  const users = (await admin.get('/api/users?actor=users.admin')).data as any[];
  if (!users.some((u) => u.id === MAKER)) {
    const wanted = [...users.filter((u) => u.id !== CHECKER), { id: MAKER, tenant: BANK, roles: ['bank configurator'] }, { id: CHECKER, tenant: BANK, roles: ['bank approver'] }];
    const prop = await admin.post('/api/users/proposals', { actor: 'users.admin', users: wanted });
    await d.api('users.approver').post(`/api/users/proposals/${prop.data.changeSet}/approve`, { actor: 'users.approver' });
  }
  if ((await versions()).filter((v) => v.kind === 'distribution').length >= 2) return;
  await d.api('shared.checker').put('/api/git', { actor: 'shared.checker', target: 'temporal', distributions: true, testDistributions: true });
  const ok = (what: string, r: any) => { if (r?.status !== 'done' && r?.status !== 200 && r?.state !== 'in-force') throw new Error(`set-up, ${what}: ${JSON.stringify(r).slice(0, 600)}`); return r; };
  const install = async (tag: string, why: string) => {
    const ask = await call(SVC, 'svc.engineer', 'POST', '/content/install', { tag, reason: why });
    if (ask.data?.status !== 'pending-approval') return ok(`install ${tag}`, ask.data);
    return ok(`install ${tag}`, (await call(SVC, 'bank.approver', 'POST', `/actions/${encodeURIComponent(ask.data.action)}/decision`, { approve: true, reason: 'Agreed: set-up before the video (synthetic)' })).data);
  };
  const distribution = async (why: string) => {
    const r = await d.api(CHECKER).post('/api/distributions', { actor: CHECKER, tenant: BANK, bank: BANK, engine: 'temporal', withSimulators: true });
    if (r.status !== 200) throw new Error(`set-up, a distribution: ${JSON.stringify(r.data).slice(0, 600)}`);
    const v = (await versions()).filter((x) => x.kind === 'distribution' && x.state === 'offered').sort((a, b) => a.release.localeCompare(b.release)).at(-1)!;
    await install(v.tag, why);
    ok(`activate ${v.releaseId}`, await consented('activate', v.releaseId, why));
  };
  /** The configuration set changed, approved, and its release with the laptop's bindings put in force. */
  const configuration = async (change: (c: any) => void, why: string) => {
    const cs = (await d.api(MAKER).get(`/api/tenants/${BANK}/configuration`)).data.configurationSet;
    const next = JSON.parse(JSON.stringify(cs));
    next.release = bump(next.release);
    change(next);
    const saved = await d.api(MAKER).post(`/api/tenants/${BANK}/configuration`, { yaml: toYaml(next) });
    const pub = await d.api(CHECKER).post('/api/publish', { keys: [saved.data.draft.key], tenant: BANK });
    if (pub.status !== 200) throw new Error(`set-up, the configuration set: ${JSON.stringify(pub.data).slice(0, 600)}`);
    const suffix = Math.max(-1, ...(await versions()).map((v) => Number(/\+bindings\.1\.0\.(\d+)$/.exec(v.release)?.[1] ?? -1))) + 1;
    const r = await d.api(CHECKER).post('/api/configuration-releases', { actor: CHECKER, tenant: BANK, bank: BANK, release: `${next.release}+bindings.1.0.${suffix}`, bindings: laptopBindings() });
    if (r.status !== 200) throw new Error(`set-up, the configuration release: ${JSON.stringify(r.data).slice(0, 600)}`);
    await install(r.data.tag, why);
  };
  await distribution('CR-E2E-001: the first distribution (synthetic)');
  await configuration((c) => {
    c.values = c.values.filter((v: any) => v.parameter !== CUT_OFF);
    c.values.push({ parameter: CUT_OFF, value: cutOff('23:30'), validFrom: '2026-11-24' });
  }, 'CR-E2E-002: the laptop configuration (synthetic)');
  await configuration((c) => {
    c.bindings = (c.bindings ?? []).filter((b: any) => b.hookPoint !== AML_POINT);
    c.bindings.push({ hookPoint: AML_POINT, hook: AML_HOOK, release: '1.0.0', validFrom: '2026-11-24' });
  }, 'CR-E2E-006: the employer AML hook (synthetic)');
  await distribution('CR-E2E-006: distribution B (synthetic)');
  // A salary file on B, waiting for the Central Bank's answer.
  const f = salaryFile();
  const r = await call(URLS.files, 'th-channel', 'POST', '/files', { tenant: BANK, message: 'uae.wps.sif', fileName: f.name, contentBase64: Buffer.from(f.text).toString('base64') });
  if (r.status !== 202) throw new Error(`set-up, a salary file: ${r.status} ${JSON.stringify(r.data).slice(0, 400)}`);
}

export async function open(d: Director) {
  // The set-up is not shown: it is cut from the video, which starts at the first scene's card.
  const t0 = d.now();
  if (!(await stackUp())) throw new Error('The laptop stack does not answer: bring it up first (deploy/laptop/README.md)');
  useStackStudio(d);
  await d.page.context().clearCookies();
  // The partners this video stops are running, and nothing waits for a consent.
  sh(`${C} start native sftp`);
  for (const p of (await call(SVC, 'bank.approver', 'GET', '/actions/pending')).data as any[])
    await call(SVC, 'bank.approver', 'POST', `/actions/${encodeURIComponent(p.data.action)}/decision`, { approve: false, reason: 'Set-up: not asked for this video (synthetic)' });
  await endOfVideo4(d);
  let vs = await versions();
  setup.A = vs.filter((v) => v.kind === 'distribution').sort((a, b) => a.release.localeCompare(b.release))[0];
  setup.B = vs.filter((v) => v.kind === 'distribution').sort((a, b) => a.release.localeCompare(b.release)).at(-1);
  if (!setup.A || setup.A === setup.B || !vs.some((v) => v.kind === 'configuration' && v.state === 'in-force'))
    throw new Error('The stack is not at the end of video 4 (two distributions, a configuration release in force)');
  // Distribution B active, as video 4 left it (an earlier run of this video rolled back and activated it again).
  if (!vs.some((v) => v.releaseId === setup.B.releaseId && v.state === 'active')) await consented('activate', setup.B.releaseId, 'CR-SVC-000: B active again (synthetic)');
  // No standing consent to rate releases yet: the video gives it.
  await consented('revoke-rate-consent', BANK, 'CR-SVC-000: no standing consent before the video (synthetic)').catch(() => undefined);
  // The bank's next configuration release, offered on GitHub: the configurator moves the cut-off for salary files, the approver
  // publishes it and builds the release with the laptop's bindings (video 4 shows this in the studio).
  const cs = (await d.api(MAKER).get(`/api/tenants/${BANK}/configuration`)).data.configurationSet;
  const now = (cs.values ?? []).find((v: any) => v.parameter === CUT_OFF)?.value?.[0]?.cutOff;
  const next = JSON.parse(JSON.stringify(cs));
  next.release = bump(next.release);
  next.values = next.values.filter((v: any) => v.parameter !== CUT_OFF);
  next.values.push({ parameter: CUT_OFF, value: cutOff(now === '23:30' ? '13:00' : '23:30'), validFrom: '2026-11-24' });
  const saved = await d.api(MAKER).post(`/api/tenants/${BANK}/configuration`, { yaml: toYaml(next) });
  if (saved.status !== 200) throw new Error(`the next configuration set: ${JSON.stringify(saved.data).slice(0, 600)}`);
  const pub = await d.api(CHECKER).post('/api/publish', { keys: [saved.data.draft.key], tenant: BANK });
  if (pub.status !== 200) throw new Error(`the next configuration set's approval: ${JSON.stringify(pub.data).slice(0, 600)}`);
  vs = await versions();
  const suffix = Math.max(0, ...vs.map((v) => Number(/\+bindings\.1\.0\.(\d+)$/.exec(v.release)?.[1] ?? -1))) + 1;
  const r = await d.api(CHECKER).post('/api/configuration-releases', { actor: CHECKER, tenant: BANK, bank: BANK, release: `${next.release}+bindings.1.0.${suffix}`, bindings: laptopBindings() });
  if (r.status !== 200) throw new Error(`the next configuration release: ${JSON.stringify(r.data).slice(0, 600)}`);
  setup.next = { tag: r.data.tag, releaseId: r.data.releaseId };
  await d.page.goto(`${URLS.service}/`);
  if (!d.dry && d.now() - t0 > 5) d.cuts.push({ from: t0 + 0.2, to: d.now() });
}

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');
  const term = (command: string, output: string) => d.terminal(command, mask(output));
  const close = () => d.closeTerminal();
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-svc-'));
  const quick = async (fn: () => Promise<void>) => {
    const was = d.pace;
    if (!d.dry) d.pace = { ...NORMAL, steps: 8, settle: 60, after: 150, highlight: 0, typeDelay: 10, tail: 0.4 };
    try { await fn(); } finally { d.pace = was; }
  };
  const until = async (what: string, ok: () => Promise<boolean>, ms = 5 * 60_000) => {
    const end = Date.now() + ms;
    while (!(await ok())) {
      if (Date.now() > end) throw new Error(`Waited ${Math.round(ms / 60000)} minutes: ${what}`);
      await new Promise((r) => setTimeout(r, 2000));
    }
  };
  const expectText = async (re: RegExp, within = main(), seconds = 30) => {
    for (let i = 0; i < seconds * 4; i++) {
      if (re.test(await within.innerText().catch(() => ''))) return;
      await p().waitForTimeout(250);
    }
    throw new Error(`The page does not show ${re}: ${(await within.innerText().catch(() => '')).slice(0, 800)}`);
  };

  // ------------------------------------------------------------ the service console
  let who = '';
  const fresh = async (hash: string) => {
    if (p().url() === `${URLS.service}/#/${hash}`) await p().goto(`${URLS.service}/`);
    await runtimeGo(d, URLS.service, hash);
  };
  const svcAs = async (as: 'service engineer' | 'bank approver', page: string) => {
    if (who !== as || !p().url().startsWith(URLS.service)) { await quick(() => runtimeSignIn(d, URLS.service, as)); who = as; }
    await fresh(`service/${page}`);
  };
  const tab = async (name: string) => {
    await d.click(p().getByRole('tab', { name, exact: true }));
    await p().locator('main h1').first().waitFor({ timeout: 15_000 });
    await d.wait(800);
  };
  const dialog = () => p().getByRole('dialog');
  /** A person's confirmation: the button, the reason typed, Confirm, and the answer on the page. */
  const confirm = async (button: any, reason: string) => {
    await d.click(button);
    await d.type(dialog().getByLabel(/Why/), reason, 20);
    await d.point(dialog(), 800);
    await d.click(dialog().getByRole('button', { name: /^Confirm/ }));
    await p().locator('[data-testid="result"], [role="alert"]').first().waitFor({ timeout: 60_000 });
    await d.wait(1200);
  };
  const result = () => p().getByTestId('result');
  const pendingRow = (match: string) => p().locator('[data-testid="pending-row"]', { hasText: match }).first();
  /** As the bank approver, in the actions page: approve (or reject) the action waiting that matches. */
  const decide = async (match: string, reason: string, approve = true) => {
    await svcAs('bank approver', 'actions');
    const row = pendingRow(match);
    await row.waitFor({ timeout: 30_000 });
    await d.point(row, 1500);
    await confirm(row.getByRole('button', { name: approve ? 'Approve' : 'Reject' }), reason);
  };
  /** As the engineer, in the actions page: an action asked for a target, with a reason. */
  const ask = async (action: string, target: string, reason: string) => {
    await svcAs('service engineer', 'actions');
    await d.select(p().locator('select[name="action"]'), action);
    await d.type(p().locator('input[name="target"]'), target, 15);
    await d.point(p().getByTestId('action-spec'), 1500);
    await d.click(p().getByRole('button', { name: 'Ask for it' }));
    await d.type(dialog().getByLabel(/Why/), reason, 20);
    await d.click(dialog().getByRole('button', { name: /^Confirm/ }));
    await p().locator('[data-testid="result"], [role="alert"]').first().waitFor({ timeout: 60_000 });
    await d.wait(1200);
  };
  const healthRow = (name: string) => p().locator(`[data-testid="health-row"][data-name^="${name}"]`).first();
  const versionRow = (tag: string) => p().locator(`[data-testid="version-row"][data-tag="${tag}"]`);
  const releaseRow = (id: string) => p().locator(`[data-testid="release-row"][data-release="${id}"]`);
  const stateOf = async (id: string) => (await releases()).find((r) => r.releaseId === id)?.state;

  // ------------------------------------------------------------ files: the File API, the SFTP server
  const send = async (f: { name: string; text: string }) => {
    const r = await call(URLS.files, 'th-channel', 'POST', '/files', { tenant: BANK, message: 'uae.wps.sif', fileName: f.name, contentBase64: Buffer.from(f.text).toString('base64') });
    if (r.status !== 202 || r.data?.disposition !== 'started') throw new Error(`POST /files: ${r.status} ${JSON.stringify(r.data).slice(0, 400)}`);
    await term(`curl -s -X POST http://localhost:8485/files -H "Authorization: Bearer $CHANNEL_TOKEN" -d '{"tenant":"${BANK}","message":"uae.wps.sif","fileName":"${f.name}","contentBase64":"..."}'`,
      JSON.stringify({ status: r.data.status, payment: r.data.payment, disposition: r.data.disposition, distribution: r.data.distribution, detail: r.data.detail }, null, 2));
    return r.data.payment as string;
  };
  const ls = (dir: string) => sh(`${C} exec -T sftp sh -c 'ls -1 ${dir} 2>/dev/null || true'`).split('\n').map((l) => l.trim()).filter(Boolean);
  const sifOf = (stem: string) => ls(CB_SIF).find((n) => n.toUpperCase() === `${stem}.SIF`);
  const sifOn = async (stem: string, ms = 3 * 60_000) => { await until(`the SIF ${stem} on the SFTP server`, async () => !!sifOf(stem), ms); return sifOf(stem)!; };
  let answers = 0;
  const ack = async (sif: string) => {
    const stem = sif.replace(/\.SIF$/i, '');
    const name = `${stem}S26${String(900 + ++answers).padStart(9, '0')}.ACK`;
    const text = `AHR,ACCEPTED,${sif}\nATR,ACCEPTED,2\n`;
    const file = path.join(work, name);
    fs.writeFileSync(file, text);
    const out = sh(`${C} exec -T -u 1001 sftp sh -c 'mkdir -p ${CB_ACK} ${SFTP_HOME}/cb/wps/writing && cat > ${SFTP_HOME}/cb/wps/writing/${name} && mv ${SFTP_HOME}/cb/wps/writing/${name} ${CB_ACK}/${name} && ls -l ${CB_ACK} | tail -3' < ${file}`);
    await term(`sftp -P 12222 th@127.0.0.1   # put ${name} upload/cb/wps/ack/`, `${text}\n$ ls -l upload/cb/wps/ack | tail -3\n${out}`);
  };
  const pinOf = async (paymentId: string) => (await call(URLS.release, 'th-pipeline', 'GET', `/payments/pin?paymentId=${encodeURIComponent(paymentId)}`)).data;
  const timeline = async (paymentId: string) => (((await call(`${URLS.ops}/ops/api`, 'ops.operator', 'GET', `/payments/${encodeURIComponent(paymentId)}`)).data?.timeline ?? []) as any[])
    .map((f) => `${f.kind}:${f.step ?? ''}:${f.code}`);
  /** The log of one payment, newest first, filtered on the page. */
  const logOf = async (paymentId: string) => {
    await svcAs('service engineer', 'logs');
    await d.type(p().locator('input[name="payment"]'), paymentId, 12);
    await d.click(p().getByRole('button', { name: 'Filter' }));
    await p().locator('[data-testid="log-row"]').first().waitFor({ timeout: 30_000 });
    await d.wait(1000);
  };
  const logRow = (text: RegExp | string) => p().locator('[data-testid="log-row"]', { hasText: text }).first();

  const state: { P?: string; f?: any; sif?: string; settled?: string; backup?: string } = {};

  const all: Scene[] = [
    // ================================================================== 0
    {
      card: ['The service console', 'For service engineers and the bank approver, page by page, on the laptop stack'],
      say: 'This video walks through the service console, page by page. The service console is where the runtime\'s service engineers look after the runtime: its health, the content it runs, its logs, its errors and their runbooks, its troubleshooting actions, its backup, and what it trusts. Every change an engineer asks for waits for the consent of a bank approver, another person. We use the laptop stack, where video four left it. Everything is synthetic: no real customer and no real account.',
    },
    // ================================================================== 1. health
    {
      card: ['1. Health', 'Every probe, its history, and a real problem on this laptop'],
      say: 'We sign in at the bank\'s identity provider, as the example service engineer. The console has nine pages, in this bar: health, content, logs, errors and runbooks, actions, backup, signers and modules, the assistant, and the audit trail. The first is health.',
      do: async () => {
        await runtimeSignIn(d, URLS.service, 'service engineer', 'service/health');
        who = 'service engineer';
        await d.point(p().getByRole('tablist', { name: 'Service' }), 3500);
      },
    },
    {
      say: 'At the top, the overall state, and when the probes last ran. Check now runs every probe again. Each row is one probe: its group, its state, what it found, and its history, one dot per run, oldest first. Hover a dot to see its time, and how long the probe took.',
      do: async () => {
        await d.click(p().getByRole('button', { name: 'Check now' }));
        await d.point(p().locator('main .summary').first(), 1500);
        await d.hover(healthRow('store').locator('[aria-label="history"] .dot').last(), 1500);
      },
    },
    {
      say: 'The store: the runtime\'s database, here SQL Server, answers. The read model, which the operations console reads: how many rows of the outbox it is behind, how old the oldest is, and how many facts it has applied. Here it is up to date.',
      do: async () => {
        await d.point(healthRow('store'), 2500);
        await d.point(healthRow('read-model'), 3000);
      },
    },
    {
      say: 'The package source: the signed versions on GitHub that this runtime may install. The trusted signers: how many keys are in use, retired or revoked; a key that is about to retire turns this row amber. And Temporal, the bank\'s engine: it accepts connections.',
      do: async () => {
        await d.point(healthRow('package-source'), 2500);
        await d.point(healthRow('trusted-signers'), 2500);
        await d.point(healthRow('temporal'), 2000);
      },
    },
    {
      say: 'The circuit breakers of the runtime\'s ports: one is known, the file transfer to the Central Bank, and none is open. In video four we saw it open while the SFTP partner was down, and close again when it came back.',
      do: async () => { await d.point(healthRow('circuit-breakers'), 3500); },
    },
    {
      say: 'One row is not green, and it is a real problem, not a staged one: the disk. This laptop\'s disk is nearly full, below the probe\'s warning level. The row carries its error code, and the code links to its entry in the error dictionary.',
      do: async () => {
        const row = healthRow('disk:');
        await d.point(row, 3000);
        await d.click(row.getByTestId('error-link').first());
        await p().getByTestId('runbook').waitFor({ timeout: 15_000 });
      },
    },
    {
      say: 'The entry says what the code means, that it is technical, and that a retry will not help. Its likely causes: logs or database files that grow, old packages kept. And the runbook: read the free space on the health page; free space at the host; never delete rows from the store. And whom to escalate to, with what.',
      do: async () => { await d.point(p().getByTestId('runbook'), 6000); },
    },
    // ================================================================== 2. content
    {
      card: ['2. Content', 'Distributions and configuration releases: offered, installed, active, in force'],
      say: 'Content. Everything the runtime runs arrives as a signed version on GitHub, and is verified against the trusted signers before it is stored. For a bank there are two kinds: its distribution, which carries the logic, and its configuration release, which carries values and bindings only. This runtime serves one bank, A D I B.',
      do: async () => {
        await tab('Content');
        await d.point(main().locator('.note').first(), 3000);
      },
    },
    {
      say: 'Offered on GitHub, each version with its state here. Distribution A, the first, is drained: no new payment takes it, and none of its payments is open. Distribution B, the one with the bank\'s hook, is active. The configuration releases: one is in force, the earlier ones are superseded, and a new one is offered.',
      do: async () => {
        await d.point(p().getByRole('table', { name: 'Versions' }), 3000);
        await d.point(versionRow(setup.A.tag), 1800);
        await d.point(versionRow(setup.B.tag), 1800);
        await d.point(versionRow(setup.next!.tag), 2200);
      },
    },
    {
      say: 'The new configuration release was built by the bank\'s approver in the studio, after the bank\'s configurator changed a value. Before installing it, the engineer verifies it. Verify asks why, as every button here does, and the answer is recorded with the engineer\'s name.',
      do: async () => {
        await confirm(versionRow(setup.next!.tag).getByRole('button', { name: 'Verify' }), 'CR-SVC-001: check the new configuration release before install (synthetic)');
        await d.point(result(), 2500);
      },
    },
    {
      say: 'Verified: its signature is a trusted signer\'s, and every file matches its hash. Now the Difference: what this release changes against the one in force. One file changed, the configuration set, and in it one value: the bank\'s cut-off for salary files.',
      do: async () => {
        await d.click(versionRow(setup.next!.tag).getByRole('button', { name: 'Difference' }));
        const diff = p().getByTestId('diff');
        await diff.waitFor({ timeout: 30_000 });
        await expectText(/cutOffs/, diff);
        await d.point(diff, 5000);
      },
    },
    {
      say: 'Install. The engineer gives the reason, the change request\'s number. Installing changes what the runtime does, so it does not happen yet: it waits for the consent of a bank approver.',
      do: async () => {
        await confirm(versionRow(setup.next!.tag).getByRole('button', { name: 'Install' }), `CR-SVC-002: put ${setup.next!.releaseId} in force (synthetic)`);
        await expectText(/waits for the consent of a bank approver/, result());
        await d.point(result(), 2500);
      },
    },
    {
      say: 'The bank approver signs in to the same console. On the actions page, under waiting for a bank approver\'s consent, is the install: who asked, when, and why. The approver approves it, with a reason of their own.',
      do: async () => { await decide(setup.next!.tag, 'Agreed: CR-SVC-002 (synthetic)'); await d.point(result(), 2000); },
    },
    {
      say: 'Done. The runtime checked every value against the bounds that each installed distribution declares, and put the release in force. Under installed, it is in force now, and the release that was in force before is superseded. Nothing was redeployed for a value.',
      do: async () => {
        await svcAs('service engineer', 'content');
        const row = releaseRow(setup.next!.releaseId);
        await row.waitFor({ timeout: 30_000 });
        await d.point(p().getByRole('table', { name: 'Releases' }), 2500);
        await d.point(row, 3000);
      },
    },
    {
      say: 'Below, the values in force: what the workers and the engine hosts read at each attempt. Each value names the configuration set it came from, and the day it took effect. Here is the new cut-off, from the new set, from today. And the bank\'s hook binding in force: its employer A M L check, after validation.',
      do: async () => {
        const values = p().getByRole('table', { name: 'Values in force' });
        await values.scrollIntoViewIfNeeded();
        await d.point(values, 2500);
        await d.point(p().locator(`[data-testid="value-row"][data-parameter="${CUT_OFF}"]`), 3000);
        await d.point(p().getByTestId('hook-bindings'), 2500);
      },
    },
    {
      say: 'A distribution changes the logic, so it can be rolled back. Say distribution B misbehaves. The engineer asks to roll back: the release that was active before, A, becomes active again, now. This too waits for the approver.',
      do: async () => {
        const row = releaseRow(setup.B.releaseId);
        await row.scrollIntoViewIfNeeded();
        await d.point(row, 1500);
        await confirm(row.getByRole('button', { name: 'Roll back' }), 'INC-SVC-003: roll back to distribution A while B is looked at (synthetic)');
        await decide('rollback', 'Agreed: INC-SVC-003 (synthetic)');
      },
    },
    {
      say: 'Distribution A is active again, and new payments take it. B is draining: no new payment takes it, and the payments already pinned to it run to their end on it, on the logic they started with. The open payments column counts them.',
      do: async () => {
        await svcAs('service engineer', 'content');
        await until('A active', async () => (await stateOf(setup.A.releaseId)) === 'active', 60_000);
        await fresh('service/content');
        await d.point(releaseRow(setup.A.releaseId), 2500);
        await d.point(releaseRow(setup.B.releaseId), 3000);
      },
    },
    {
      say: 'B is fine after all, so the engineer activates it again, and the approver consents. B is active, and A drains. Drain and remove are the other buttons here: remove is refused while a payment pinned to the release is open.',
      do: async () => {
        await confirm(releaseRow(setup.B.releaseId).getByRole('button', { name: 'Activate' }), 'INC-SVC-003: B is fine, activate it again (synthetic)');
        await decide('activate', 'Agreed: INC-SVC-003 (synthetic)');
        await svcAs('service engineer', 'content');
        await until('B active', async () => (await stateOf(setup.B.releaseId)) === 'active', 60_000);
        await fresh('service/content');
        await d.point(releaseRow(setup.B.releaseId), 2500);
        await d.point(releaseRow(setup.A.releaseId), 2000);
      },
    },
    {
      say: 'Last on this page: payments pinned but never started. The front door pins each payment to a distribution when it accepts it. If its start then never reaches an engine, the pin keeps the distribution from draining. Here there is none. If there were, end pin would end it as abandoned, with the approver\'s consent.',
      do: async () => {
        const none = p().getByTestId('no-stuck-pins');
        await none.scrollIntoViewIfNeeded();
        await d.point(main().getByRole('heading', { name: 'Payments pinned but never started' }), 1500);
        await d.point(none, 2500);
      },
    },
    // ================================================================== 3. consents
    {
      card: ['3. Consents', 'Asked by a service engineer, given or refused by a bank approver'],
      say: 'Every action that changes the system works the same way. The engineer asks, with a reason. A bank approver, another person, consents or refuses. The one who asked can never consent. Here the engineer asks to drain distribution B.',
      do: async () => { await ask('drain', setup.B.releaseId, 'CR-SVC-004: drain B before the weekend (synthetic)'); await d.point(result(), 2500); },
    },
    {
      say: 'The engineer sees it waiting, with no button to approve it. The approver signs in, reads the reason, and refuses it: no drain is planned this week. A refusal is recorded too, with its reason.',
      do: async () => {
        await d.point(pendingRow('drain'), 2500);
        await decide('drain', 'Refused: no drain is planned this week (synthetic)', false);
        await d.point(result(), 2000);
      },
    },
    {
      say: 'One consent can stand. The bank\'s exchange-rate feed publishes a configuration release with the day\'s rates, many times a day. The approver may give a standing consent to those releases: they then go in force without a consent each. Every other release still waits for one. The engineer asks for it, for the bank.',
      do: async () => { await ask('grant-rate-consent', BANK, 'CR-SVC-005: let the rate feed\'s releases go in force (synthetic)'); },
    },
    {
      say: 'The approver gives it. From now on the feed\'s rate releases go in force on this standing consent, until an approver revokes it, the same way.',
      do: async () => { await decide('grant-rate-consent', 'Agreed: rates only, CR-SVC-005 (synthetic)'); await d.point(result(), 2500); },
    },
    // ================================================================== 4. a problem diagnosed
    {
      card: ['4. A problem, diagnosed', 'A salary file stops at one step'],
      say: 'Now a problem, diagnosed from the console. On this laptop we stop the native host, the service that runs the compiled activities, such as the one that renders the Central Bank\'s salary file. Then the bank\'s channel sends a salary file.',
      do: async () => {
        await term(`${C} stop native`, sh(`${C} stop native`));
        await d.wait(4000);
        state.f = salaryFile();
        state.P = await send(state.f);
        await d.skip('The file runs to the step that fails', async () => until('the render step parked', async () => (await timeline(state.P!)).some((x) => /render:stuck/.test(x)), 3 * 60_000), 6);
      },
    },
    {
      say: 'Someone at the bank says the file never reached the Central Bank. The engineer opens the log, and filters it by the payment. Newest first: the checks passed; then the render step failed, and its attempts were spent; and the step parked. A step that parks waits, for a business day, for an operator to retry it.',
      do: async () => {
        await close();
        await logOf(state.P!);
        await d.point(logRow('step:error'), 3000);
        await d.point(logRow('step:stuck'), 3000);
      },
    },
    {
      say: 'The log names the error: the native host was unreachable, and the error is retryable. So the step itself is fine: something it needs is down. The engineer checks the native host, and on this laptop starts it again.',
      do: async () => {
        await d.point(logRow('step:error').getByTestId('step-error'), 3000);
        await term(`${C} start native`, sh(`${C} start native`));
        await until('the native host healthy', async () => sh('docker inspect -f {{.State.Health.Status}} $(docker compose -f deploy/laptop/compose.yaml ps -q native)') === 'healthy', 2 * 60_000);
        // The host loads the active distribution's modules as it starts (Issue I-354): wait for it, as its /modules says, read
        // from the workers' container with their own token, which is never printed.
        const modules = () => sh(`${C} exec -T workers node -e 'fetch(process.argv[1],{headers:{authorization:"Bearer "+require("fs").readFileSync(process.argv[2],"utf8").trim()}}).then(r=>r.json()).then(j=>console.log((j.loaded||[]).map(x=>x.distribution).join(" ")))' http://native:7300/modules /var/lib/transacthub/tokens/th-front-door.jwt`);
        await until('the native host loaded the active distribution', async () => modules().includes(setup.B.releaseId), 2 * 60_000);
        await d.wait(1500);
      },
    },
    {
      say: 'Now the parked step can go on. On the actions page, retry an attempt gives the parked step one more attempt. Re-drive runs it again with its whole retry policy. The engineer asks for a retry of this payment.',
      do: async () => {
        await close();
        await ask('retry-attempt', state.P!, 'INC-SVC-006: the native host was down and is back; retry the render (synthetic)');
      },
    },
    {
      say: 'A retry touches a running payment, so the approver consents. The control goes to the payment\'s engine through its engine host, which records that it delivered it.',
      do: async () => { await decide('retry-attempt', 'Agreed: INC-SVC-006 (synthetic)'); await d.point(result(), 2500); },
    },
    {
      say: 'Back in the log. The retry was asked, approved, done, and delivered to the engine. Then the render step ran again, and passed: the salary information file was rendered, encrypted and handed to the File API, which put it on the Central Bank\'s folder.',
      do: async () => {
        await until('the payment sent its file', async () => (await timeline(state.P!)).some((x) => /submit\.send:accepted/.test(x)), 3 * 60_000);
        await logOf(state.P!);
        await d.point(logRow('signal:delivered'), 2500);
        await d.point(logRow('render (uae.wps.map.sif-out'), 2500);
        await d.point(logRow('submit.send'), 2500);
      },
    },
    // ================================================================== 5. resend a kept file
    {
      card: ['5. Resend a kept file', 'The same bytes, never rendered or signed again'],
      say: 'One more turn. The Central Bank says its folder lost the file. On this laptop we delete it from the S F T P folder. The payment still waits for the Central Bank\'s answer.',
      do: async () => {
        state.sif = await sifOn(state.f.stem);
        await term(`sftp -P 12222 th@127.0.0.1   # rm upload/cb/wps/sif/${state.sif}`, sh(`${C} exec -T -u 1001 sftp sh -c 'rm -f ${CB_SIF}/${state.sif} && ls -l ${CB_SIF} | tail -3'`));
        await d.wait(2500);
      },
    },
    {
      say: 'Resend a file puts the same bytes the runtime kept on the file transfer port again. It is never rendered or signed again, so what the Central Bank receives is exactly what it would have received. The engineer asks for it.',
      do: async () => {
        await close();
        await ask('resend-file', state.P!, 'INC-SVC-007: the Central Bank\'s folder lost the SIF; send the kept file again (synthetic)');
      },
    },
    {
      say: 'The approver consents, and the file is back on the Central Bank\'s folder, with the same name and the same content.',
      do: async () => {
        await decide('resend-file', 'Agreed: INC-SVC-007 (synthetic)');
        await sifOn(state.f.stem, 2 * 60_000);
        await term(`sftp -P 12222 th@127.0.0.1   # ls upload/cb/wps/sif`, `${sh(`${C} exec -T sftp sh -c 'ls -l ${CB_SIF} | tail -3'`)}\n\n$ head -c 160 upload/cb/wps/sif/${state.sif}\n${sh(`${C} exec -T sftp head -c 160 ${CB_SIF}/${state.sif}`)}`);
        await d.wait(2000);
      },
    },
    {
      say: 'The Central Bank answers with an ACK, and the payment runs to its end: settled. A step that failed and a file that was lost cost a delay, and nothing else.',
      do: async () => {
        await ack(state.sif!);
        await d.skip('The payment reads the ACK and runs to its end', async () => until('the payment ended', async () => !!(await pinOf(state.P!))?.ended, 5 * 60_000), 6);
        state.settled = (await pinOf(state.P!))?.ended;
        if (state.settled !== 'settled') throw new Error(`The payment ended ${state.settled}`);
        await close();
        await logOf(state.P!);
        await d.point(logRow('payment.end'), 3000);
      },
    },
    {
      say: 'The catalogue, at the foot of the actions page, lists every troubleshooting action: what it does, whether it is built, and whether it needs an approver. Among them: requeue a dead-letter job, on Flowable; reset an activity, on Temporal; release or expire a hold; pause and resume intake for a scheme; drain or restart a worker; and rebuild the read model.',
      do: async () => {
        await svcAs('service engineer', 'actions');
        const cat = p().getByRole('table', { name: 'Catalogue' });
        await cat.scrollIntoViewIfNeeded();
        await d.point(cat.locator('tr', { hasText: 'requeue-dead-letter' }), 2000);
        await d.point(cat.locator('tr', { hasText: 'reset-activity' }), 1500);
        await d.point(cat.locator('tr', { hasText: 'pause-intake' }), 1500);
        await d.scroll(500);
        await d.wait(2000);
      },
    },
    // ================================================================== 6. logs and the audit chain
    {
      card: ['6. Logs and the audit chain', 'Every fact, and the console\'s own record'],
      say: 'The log has more filters: an error code, a leg, a scheme, the tenant, a payment, a trace, and a time from and to. Filtered by the error code we just met, it lists every step that failed with it, for any payment. Each line links to its payment in the operations console, and, when the runtime traces, to its trace.',
      do: async () => {
        await svcAs('service engineer', 'logs');
        await d.point(main().locator('.filters'), 2500);
        await d.type(p().locator('input[name="code"]'), 'NativeHostUnreachable', 30);
        await d.click(p().getByRole('button', { name: 'Filter' }));
        await p().locator('[data-testid="log-row"]').first().waitFor({ timeout: 30_000 });
        await d.wait(1200);
        await d.point(p().locator('[data-testid="log-row"]').first(), 3000);
      },
    },
    {
      say: 'The audit page is the console\'s own record: every request, consent, refusal and result, newest first, with who, what and why. Each fact is chained to the one before it by its hash, and the line at the top says the chains verify. A fact changed afterwards would break its chain, and show here.',
      do: async () => {
        await tab('Audit');
        await p().locator('[data-testid="audit-row"]').first().waitFor({ timeout: 30_000 });
        await d.point(p().getByTestId('chain'), 3000);
        await d.point(p().locator('[data-testid="audit-row"]').first(), 2500);
      },
    },
    {
      say: 'Filtered by person, the bank approver: each consent and refusal of this video, with its reason.',
      do: async () => {
        await d.type(p().getByLabel('Person'), 'bank.approver', 30);
        await d.click(p().getByRole('button', { name: 'Filter' }));
        await d.wait(1500);
        await d.point(p().getByRole('table', { name: 'Audit trail' }), 3500);
      },
    },
    // ================================================================== 7. the error dictionary
    {
      card: ['7. Errors and runbooks', 'The error dictionary every page links to'],
      say: 'Errors and runbooks: the whole error dictionary. Each code with its scope, the platform\'s or a scheme\'s, its title, whether it is business or technical, whether a retry can help, and its I S O 20022 reason code where it has one.',
      do: async () => {
        await tab('Errors and runbooks');
        await d.point(p().getByRole('table', { name: 'Errors' }), 3500);
      },
    },
    {
      say: 'Search for transient. A temporary failure, that the engine retries. Its runbook: check the port\'s health, and the attempts on the payment\'s timeline; wait for the retries; and if they are spent, retry the attempt once the party answers again, the action we just used. Each troubleshooting action the runbook offers links to the actions page.',
      do: async () => {
        await d.type(p().locator('input[name="q"]'), 'transient', 40);
        await d.wait(800);
        await d.click(p().getByRole('link', { name: 'TransientError', exact: true }));
        const rb = p().getByTestId('runbook');
        await rb.waitFor({ timeout: 15_000 });
        await d.point(rb, 6000);
      },
    },
    {
      say: 'A business error reads the same way. Insufficient funds: business, not retryable, I S O reason A M zero four. Its runbook says not to retry: the operator decides the payment.',
      do: async () => {
        await fresh('service/errors/AM04');
        await p().getByTestId('runbook').waitFor({ timeout: 15_000 });
        await d.point(p().getByTestId('runbook'), 4500);
      },
    },
    // ================================================================== 8. backup
    {
      card: ['8. Backup', 'A signed backup of the configuration, verified and tried'],
      say: 'Backup. A signed backup holds the ids of the releases, their activations, the configuration values, the bindings with secret references only, never a value, the role map and the trusted signers. The engineer exports one, with a reason.',
      do: async () => {
        await tab('Backup');
        await d.point(main().locator('.note').first(), 2500);
        const dl = p().waitForEvent('download', { timeout: 60_000 });
        await d.click(p().getByRole('button', { name: 'Export a backup' }));
        await d.type(dialog().getByLabel(/Why/), 'OPS-SVC-008: weekly backup (synthetic)', 20);
        await d.click(dialog().getByRole('button', { name: /^Confirm/ }));
        const file = await dl;
        state.backup = path.join(work, 'transacthub-backup.zip');
        await file.saveAs(state.backup);
        await p().getByTestId('backup-result').waitFor({ timeout: 30_000 });
        await d.wait(1200);
      },
    },
    {
      say: 'A backup is checked before it is used. Choose the file and verify it: its signature and every entry\'s hash hold. Then a dry run of a restore: what it would install and set, changing nothing. A real restore goes only into an empty or a test runtime, after a dry run, and only with an approver\'s consent.',
      do: async () => {
        await p().locator('input[name="backup"]').setInputFiles(state.backup!);
        await d.wait(800);
        await d.click(p().getByRole('button', { name: 'Verify', exact: true }));
        await expectText(/"verified"|signedBy|keyId/i, p().getByTestId('backup-result'));
        await d.point(p().getByTestId('backup-result'), 3000);
        await d.click(p().getByRole('button', { name: 'Dry run' }));
        await d.type(dialog().getByLabel(/Why/), 'OPS-SVC-008: try the backup (synthetic)', 20);
        await d.click(dialog().getByRole('button', { name: /^Confirm/ }));
        await expectText(/dryRun|dry/i, p().getByTestId('backup-result'));
        await d.point(p().getByTestId('backup-result'), 3500);
      },
    },
    // ================================================================== 9. signers and modules
    {
      card: ['9. Signers and native modules', 'What the runtime trusts, checked by hash'],
      say: 'Signers and modules: what this runtime trusts. The trusted signers: each key by its id, in use, retired or revoked, and what it may sign. Every version is verified against them before it is stored. A new key is added, and an old one retired, with the action rotate a trusted key, with consent.',
      do: async () => {
        await tab('Signers and modules');
        await p().locator('[data-testid="signer-row"]').first().waitFor({ timeout: 30_000 });
        await d.point(p().getByRole('table', { name: 'Trusted signers' }), 4000);
      },
    },
    {
      say: 'A distribution\'s native modules are its compiled activities: the platform\'s, and the W P S scheme\'s. Each file is in the signed bill of materials with its S H A two five six. The runtime checks every hash when it installs the distribution, and the native host checks them again when it loads it. A file that does not match is refused.',
      do: async () => {
        const b = p().locator('[data-testid="native-distribution"]', { hasText: setup.B.releaseId });
        await b.scrollIntoViewIfNeeded();
        await d.point(b, 3000);
        await d.point(b.locator('[data-testid="native-file-row"]').first(), 2500);
      },
    },
    {
      say: 'Re-verify checks a stored distribution again, against the signers trusted now. It changes nothing, so it needs no approver, and its result is recorded on the release\'s chain.',
      do: async () => {
        const b = p().locator('[data-testid="native-distribution"]', { hasText: setup.B.releaseId });
        await confirm(b.getByRole('button', { name: 'Re-verify' }), 'OPS-SVC-009: check B against the signers trusted now (synthetic)');
        await d.point(result(), 2500);
      },
    },
    // ================================================================== 10. the assistant
    {
      card: ['10. The assistant', 'Off, until the bank\'s data protection officer approves it'],
      say: 'Last, the assistant. It reads the runbooks, the logs and a payment\'s facts, with personal data masked before anything is sent to the model, and it may propose an action, which a person confirms on the actions page. On this runtime it is off, and it stays off until the bank\'s data protection officer approves it. Until then, the engineer uses the runbooks directly.',
      do: async () => {
        await tab('Assistant');
        await expectText(/The assistant is off/);
        await d.point(main().getByText(/The assistant is off/).first(), 4000);
      },
    },
    {
      card: ['That is the service console', 'Health, content, consents, a problem diagnosed, resend, logs and audit, runbooks, backup, signers and modules'],
      say: 'That is the service console. Health, with every probe, and a real problem with its runbook. Content: verified, compared, installed, put in force, rolled back and activated again. Consents asked by an engineer and given or refused by an approver, and a standing one for rates. A parked step found in the log and retried, a lost file resent, and the payment settled. The log, the audit chain, the error dictionary, the backup, what the runtime trusts, and the assistant, waiting for its approval.',
    },
  ];
  return d.dry && process.env.TH_DEMO_FROM ? all.slice(Number(process.env.TH_DEMO_FROM)) : all;
}
