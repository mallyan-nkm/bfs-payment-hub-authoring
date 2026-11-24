import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { parseYaml } from '../src/core/yaml.js';
import { SLOW, type Director, type Scene } from './record.js';
import { BANK, LAPTOP, ROOT, URLS, call, compose, runtimeGo, runtimeSignIn, sh, stackUp, useStackStudio } from './stack.js';

// Video 2, the installation: the laptop stack (deploy/laptop) on Fluent UI 2. What you need and why; the images built and the
// stack started, each service and the health check; the first sign-in to each UI (the studio, the operations console, the
// registers, the service console, the test bench), light and dark; the bank's first distribution installed and its configuration
// release put in force, each with a bank approver's consent; a salary file on the test bench (on the reference interpreter: the
// bench's Temporal run on the laptop is Issue I-331) and one real payment through the File API, its SIF on the SFTP server and the
// Central Bank's ACK left there; then how to stop, reset and move to PostgreSQL. Plain short narration.
//
// It runs on a fresh stack brought up beforehand with a compose project of its own (COMPOSE_PROJECT_NAME) and the secrets in the
// environment (deploy/laptop/README.md). The commands shown in the terminal panel are run for real while recording, and every
// secret value in their output is replaced with stars. TH_DEMO_BUILD_LOG names the log of the build made before (its last lines
// are shown; the build itself takes minutes). The last chapter stops the stack, removes its volumes and starts it again on
// PostgreSQL, for real, when recording; a dry run leaves the stack as it is unless TH_DEMO_RESET=1.

export const NAME = '2-installation';
export const TITLE = 'TransactHub: install the laptop stack, sign in, and put the first distribution in force';
export const PACE = SLOW;

const MAKER = `${BANK}.maker`;
const CHECKER = `${BANK}.checker`;
const SVC = `${URLS.service}/service/api`;
const C = 'docker compose -f deploy/laptop/compose.yaml';
const PG = `${C} -f deploy/laptop/compose.postgres.yaml`;
const EVENTS = `${C} -f deploy/laptop/compose.events.yaml --profile events`;
const SFTP_HOME = '/home/th/upload';
const CB_SIF = `${SFTP_HOME}/cb/wps/sif`;
const CB_ACK = `${SFTP_HOME}/cb/wps/ack`;
/** The run's own minute, so that the file is new to the File API (it refuses a duplicate). */
const STAMP = (() => { const t = new Date(); return `${String(t.getUTCHours()).padStart(2, '0')}${String(t.getUTCMinutes()).padStart(2, '0')}`; })();
/** A synthetic salary file of the bank's employer, with no `# file:` line (as demo/end-to-end.ts makes it). */
function salaryFile(): { name: string; stem: string; text: string } {
  const sample = fs.readFileSync(path.join(ROOT, 'studio', 'fixtures', 'samples', 'wps-sif-no-file-line.sif'), 'utf8');
  const stem = `0000001234567261117${STAMP}00`;
  return { name: `${stem}.sif`, stem, text: sample.replace(',0830,', `,${STAMP},`).replace('REF-0001', `REF-${STAMP}`) };
}
const NAMES = "env | grep -o '^TH_LAPTOP_[A-Z_]*' | grep -v '_PORT$\\|_URL$\\|_BANK$\\|_WORKERS_BENCH$' | sort";
/** The laptop's bindings of the bank's file transfer, alerts and events (deploy/laptop/banks/<bank>), as the insert-only test builds them in. */
const laptopBindings = () => ['file-transfer.yaml', 'events.yaml'].map((f) => path.join(LAPTOP, 'banks', BANK, f)).filter((f) => fs.existsSync(f))
  .flatMap((f) => parseYaml(fs.readFileSync(f, 'utf8')).bindings);

export async function open(d: Director) {
  if (!(await stackUp())) throw new Error('The laptop stack does not answer: bring it up first (deploy/laptop/README.md)');
  useStackStudio(d);
  await d.page.context().clearCookies();
  // Before the camera: the bank's two studio users, added by one administrator and approved by another (as the laptop test does),
  // and the bank's first distribution, built and published by the studio as the bank approver's approval of its last change
  // would have built it.
  const admin = d.api('users.admin');
  const users = (await admin.get('/api/users?actor=users.admin')).data as any[];
  if (!users.some((u) => u.id === MAKER)) {
    const wanted = [...users.filter((u) => u.id !== CHECKER), { id: MAKER, tenant: BANK, roles: ['bank configurator'] }, { id: CHECKER, tenant: BANK, roles: ['bank approver'] }];
    const prop = await admin.post('/api/users/proposals', { actor: 'users.admin', users: wanted });
    await d.api('users.approver').post(`/api/users/proposals/${prop.data.changeSet}/approve`, { actor: 'users.approver' });
  }
  await d.api('shared.checker').put('/api/git', { actor: 'shared.checker', target: 'temporal', distributions: true, testDistributions: true });
  const versions = (await call(SVC, 'svc.engineer', 'GET', '/content/versions')).data as any[];
  if (!versions.some((v) => v.kind === 'distribution' && v.bank === BANK)) {
    const r = await d.api(CHECKER).post('/api/distributions', { actor: CHECKER, tenant: BANK, bank: BANK, engine: 'temporal', withSimulators: true });
    if (r.status !== 200) throw new Error(`distribution A: ${JSON.stringify(r.data)}`);
  }
  await d.page.goto(`${URLS.studio}/`);
  await d.page.waitForLoadState('networkidle');
}

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const term = (command: string, output: string) => d.terminal(command, output);
  const close = () => d.closeTerminal();
  const nav = () => d.point(p().locator('nav.sidebar, nav.side, nav').first(), 1800);
  const studioGo = async (hash: string) => { await p().goto(`${URLS.studio}/#/${hash}`); await p().waitForLoadState('networkidle'); await d.wait(d.pace.after); };
  const theme = async (which: 'Dark' | 'Light') => {
    await d.click(p().getByTestId('theme-menu').first());
    await d.click(p().getByRole('menuitemradio', { name: which }));
    await d.wait(1200);
  };
  const seen: Record<string, string> = {};
  const fresh = async (base: string, hash: string) => {
    if (p().url() === `${base}/#/${hash}`) await p().goto(`${base}/`);
    await runtimeGo(d, base, hash);
  };
  const svcAs = async (who: 'service engineer' | 'bank approver', page: string) => {
    if (seen.svc !== who || !p().url().startsWith(URLS.service)) { await runtimeSignIn(d, URLS.service, who); seen.svc = who; }
    await fresh(URLS.service, `service/${page}`);
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
  const state: { A?: any; R1?: any; P1?: string; sif?: { name: string; stem: string; text: string }; sent?: string } = {};
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-install-'));
  const pinOf = async (paymentId: string) => (await call(URLS.release, 'th-pipeline', 'GET', `/payments/pin?paymentId=${encodeURIComponent(paymentId)}`)).data;
  const until = async (what: string, ok: () => Promise<boolean>, ms = 15 * 60_000) => {
    const end = Date.now() + ms;
    while (!(await ok())) {
      if (Date.now() > end) throw new Error(`Waited ${Math.round(ms / 60000)} minutes: ${what}`);
      await new Promise((r) => setTimeout(r, 3000));
    }
  };
  const ls = (dir: string) => sh(`${C} exec -T sftp sh -c 'ls -1 ${dir} 2>/dev/null || true'`).split('\n').map((l) => l.trim()).filter(Boolean);
  /** The operator's upload page: the message, the file, Upload; the payment it started. */
  const upload = async (message: RegExp, name: string, text: string) => {
    await runtimeSignIn(d, URLS.ops, 'operator', 'upload');
    const file = path.join(work, name);
    fs.writeFileSync(file, text);
    const sel = p().getByLabel('Message');
    for (let i = 0; i < 100 && !(await sel.locator('option').count()); i++) await p().waitForTimeout(200);
    const value = await sel.locator('option').evaluateAll((os: any[], src: string) => os.find((o) => new RegExp(src).test(o.textContent))?.value, message.source);
    if (!value) throw new Error(`The upload page offers no message like ${message}`);
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
  const buildLog = () => {
    const f = process.env.TH_DEMO_BUILD_LOG;
    const tail = f && fs.existsSync(f) ? fs.readFileSync(f, 'utf8').split('\n').filter((l) => /^(native module|Successfully|#\d+ (naming|exporting)|.*writing image)/.test(l)).slice(-6).map((l) => l.replace(/\/home\/[^ ]*\/deploy/g, 'deploy')) : [];
    return [...tail, '', sh("docker image ls --format '{{.Repository}}:{{.Tag}}   {{.Size}}' | grep '^th-laptop-'")].join('\n');
  };
  const hostKey = () => sh(`for k in ed25519 rsa; do ${C} exec -T sftp ssh-keygen -lf /etc/ssh/ssh_host_\${k}_key.pub | cut -d' ' -f2; done | paste -sd,`);
  // A recording resets the stack for real; a dry run (the demo videos test among them) leaves it, unless TH_DEMO_RESET=1.
  const reset = d.dry ? process.env.TH_DEMO_RESET === '1' : true;

  return [
    // ================================================================== 0
    {
      card: ['Install TransactHub on a laptop', 'The studio and one bank\'s runtime, in Docker, on Fluent UI 2.'],
      say: 'In this video we install TransactHub on a laptop. We build the images, start the services, and sign in to each screen. Then we install the bank\'s first distribution, put its configuration in force, and run a test payment. Everything here is synthetic.',
    },
    // ================================================================== 1. what you need
    {
      card: ['1. What you need', 'Docker, and the secrets in your shell'],
      say: 'You need Docker, with the compose plugin, and about eight gigabytes of free disk. That is all. Every service runs in a container, so you install no database and no SDK.',
      do: async () => { await term('docker version --format \'{{.Server.Version}}\' && docker compose version', sh("docker version --format 'Docker {{.Server.Version}}' && docker compose version")); },
    },
    {
      say: 'You also need passwords and client secrets: for the databases, for the synthetic users, and for the machines that call each other. You make them yourself, at random, in your shell.',
      do: async () => { await term('export TH_LAPTOP_SA_PASSWORD="$(openssl rand -base64 24)"   # and one for each name below', sh(NAMES)); },
    },
    {
      say: 'Why in the shell? A secret in a file ends up in a commit, a backup or an image. So no file in the repository holds one, and no image holds one. Compose reads them from the environment when the stack starts. Here we show the names only, never the values.',
      do: async () => { await d.wait(600); },
    },
    {
      say: 'The keys are different. A one-off container makes the signing keys into a volume on first start. The build makes its own key to sign the native modules, for this laptop only.',
      do: async () => { await d.wait(400); },
    },
    // ================================================================== 2. build and start
    {
      card: ['2. Build and start', 'Three images, then one command'],
      say: 'First, build the images from the repository root. The build script makes three: the studio, the runtime, and Temporal on SQL Server. With no options it builds everything inside Docker. It takes a few minutes, so we show its last lines.',
      do: async () => { await term('deploy/laptop/build.sh', buildLog()); },
    },
    {
      say: 'Check the compose file. Then start the SFTP server first, and pin its host keys: the File API trusts that server by its keys, and nothing else.',
      do: async () => {
        await term(`${C} config -q && echo valid`, compose('config -q && echo valid'));
        await d.wait(2500);
        await term(`${C} up -d --wait sftp\nexport TH_LAPTOP_SFTP_HOST_KEY=$(... ssh-keygen -lf ...)`, `${compose('up -d --wait sftp 2>&1 | tail -3')}\n\nTH_LAPTOP_SFTP_HOST_KEY=${hostKey()}`);
      },
    },
    {
      say: 'Now start everything. Up, dash d, dash dash wait, returns when every service is healthy. The first start takes a few minutes. SQL Server starts, the migrations run once as the database owner, and the runtime\'s login is granted read and insert only.',
      do: async () => { await term(`${C} up -d --wait`, compose('up -d --wait --wait-timeout 300 2>&1 | tail -22')); },
    },
    {
      say: 'Here is what runs. The studio, on port 4317: where content is designed. Then the runtime: the release API, the service console, the operations console, and the test bench, on 8481 to 8484.',
      do: async () => { await term(`${C} ps`, compose("ps --format 'table {{.Service}}\\t{{.Status}}\\t{{.Ports}}'")); },
    },
    {
      say: 'The engine host runs payments on Temporal. The workers run each distribution\'s activities. The native host runs the compiled modules a distribution carries. The File API is the only door for files, in and out.',
      do: async () => { await d.point(p().locator('#__term'), 1200); },
    },
    {
      say: 'Machine auth keeps fresh tokens for the services that call the File API. The bank I D P is the bank\'s development sign-in, with synthetic users. SQL Server holds the store and Temporal\'s data. The SFTP server plays the bank\'s partners.',
      do: async () => { await d.wait(400); },
    },
    {
      say: 'Some services start only when you ask. DTFx, Elsa and Flowable each have an engine host of their own, under a profile. The events profile adds two brokers: MQTT for the operators\' alerts, and Kafka for the payment events.',
      do: async () => { await term(`${EVENTS} config --services | grep -E 'mosquitto|kafka'`, sh(`${EVENTS} config --services | grep -E 'mosquitto|kafka'`)); },
    },
    {
      say: 'Every image has a health check, and runs as an unprivileged user. Every port is on this machine only. To check the stack at any time, ask each container its health.',
      do: async () => {
        await term(`docker ps --filter label=com.docker.compose.project=$COMPOSE_PROJECT_NAME --format '{{.Names}}\\t{{.Status}}'`,
          sh(`docker ps --filter label=com.docker.compose.project=${process.env.COMPOSE_PROJECT_NAME ?? 'laptop'} --format '{{.Names}}\\t{{.Status}}' | sort`));
      },
    },
    // ================================================================== 3. first sign-in
    {
      card: ['3. Sign in to each screen', 'The studio, the operations console, the registers, the service console, the test bench'],
      say: 'Now the screens. First, the studio. On a laptop it has its own development sign-in, with synthetic users. At a bank, the sign-in is the bank\'s identity provider.',
      do: async () => {
        await close();
        await p().goto(`${URLS.studio}/`);
        await p().waitForLoadState('networkidle');
        await d.click(p().getByRole('link', { name: 'Sign in' }).first());
        await p().waitForLoadState('networkidle');
        await d.wait(1200);
      },
    },
    {
      say: 'We sign in as the workflow designer. The navy top bar holds the search, the scope badges and the theme menu. The side bar lists what this role may open: the designer, the editors, the catalogue, the tests, and more.',
      do: async () => {
        await d.click(p().getByRole('button', { name: 'shared.designer', exact: true }));
        await p().getByLabel('Signed in as').waitFor();
        d.user = 'shared.designer';
        await d.wait(800);
        await d.point(p().locator('header').first(), 1500);
        await nav();
      },
    },
    {
      say: 'The workflow designer shows the YAML beside the canvas. Every screen follows your system\'s light or dark setting, or you choose one in the theme menu. Here is dark, and back to light.',
      do: async () => { await studioGo('designer'); await d.wait(800); await theme('Dark'); await d.wait(1500); await theme('Light'); },
    },
    {
      say: 'A bank\'s configurator sees the bank workspace instead, where the bank sets its values and binds its hooks.',
      do: async () => { await d.as('', MAKER); await studioGo('bank'); await nav(); },
    },
    {
      say: 'Next, the runtime\'s consoles. They are one web app, and they share one sign-in at the bank\'s identity provider. On a laptop, that is a development provider with six synthetic users. The runtime keeps no users of its own.',
      do: async () => {
        await p().goto(`${URLS.ops}/`);
        await p().waitForLoadState('load');
        await d.click(p().getByRole('button', { name: 'Sign in' }));
        await p().waitForURL((u: URL) => u.port === '8480');
        await d.wait(1200);
        await d.point(p().locator('body'), 1500);
      },
    },
    {
      say: 'We sign in as an operator. The operations console is where the bank\'s operators see running payments. There are none yet. The badge in the top bar says whether the read model has caught up with the store.',
      do: async () => {
        await d.click(p().getByRole('button', { name: /Sign in as Example operator/ }));
        await p().waitForURL((u: URL) => u.port === '8483' && !u.searchParams.has('code'));
        await p().getByRole('button', { name: 'Sign out' }).waitFor();
        await runtimeGo(d, URLS.ops, 'payments');
        await d.point(p().locator('[data-testid="lag"]'), 1500);
        await nav();
      },
    },
    {
      say: 'The same console in dark. The work queue, approvals, reports and the rest are in the side bar.',
      do: async () => { await theme('Dark'); await runtimeGo(d, URLS.ops, 'work'); await d.wait(1200); await theme('Light'); },
    },
    {
      say: 'The registers hold the bank\'s customer data, such as the corporate registration. It never goes to the studio or to GitHub. A register maker proposes a change, and a bank approver consents.',
      do: async () => {
        await runtimeSignIn(d, URLS.ops, 'register maker', 'registers');
        const first = p().locator('main a, main [role="tab"]').first();
        if (await first.count()) await d.click(first);
        await d.wait(1200);
      },
    },
    {
      say: 'The service console is for the bank\'s service engineers. Health shows each part of the runtime: the store, the read model, the package source, the keys, the engine and each bound port. On a laptop some rows are red, as the bank\'s own hosts are placeholders.',
      do: async () => { await svcAs('service engineer', 'health'); await d.point(p().getByRole('table', { name: 'Health' }), 2500); },
    },
    {
      say: 'Content lists the signed versions offered on GitHub. The studio has already published this bank\'s first distribution. We install it in a moment.',
      do: async () => { await fresh(URLS.service, 'service/content'); await d.point(p().getByRole('table', { name: 'Versions' }), 2000); },
    },
    {
      say: 'Last, the test bench, for the bank\'s testers. It runs a payment on the real engines, beside the interpreter as a reference. We sign in as the tester.',
      do: async () => { await runtimeSignIn(d, URLS.testbench, 'tester', 'testbench'); await d.scroll(300); },
    },
    // ================================================================== 4. first distribution
    {
      card: ['4. The first distribution', 'Installed, activated and put in force, each with a bank approver\'s consent'],
      say: 'A distribution is a signed, complete copy of everything the bank runs, built by the studio. The service engineer opens the service console. Difference shows what it would change. Nothing is active yet, so everything is new.',
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
      say: 'The bank approver signs in, sees what waits, and approves it, with a reason. The runtime checks the signature, and the hash of every native module, and stores the distribution. It only ever adds rows.',
      do: async () => { await consent('Agreed: CR-DEMO-001 (synthetic)'); await svcAs('service engineer', 'content'); await d.point(releaseRow(state.A.releaseId), 1500); },
    },
    {
      say: 'Installing is not using. The engineer activates it, and the approver consents again. From now on, new payments of this bank take this distribution.',
      do: async () => {
        await confirm(releaseRow(state.A.releaseId).getByRole('button', { name: 'Activate' }), `CR-DEMO-001: activate ${state.A.release} (synthetic)`);
        await consent('Agreed: activate (synthetic)');
        await svcAs('service engineer', 'content');
        await d.point(releaseRow(state.A.releaseId), 2000);
      },
    },
    {
      say: 'Next, the bank\'s configuration release: its values and bindings, never code. On this laptop every partner of the bank is the one SFTP server, so the bank approver builds the release with the laptop\'s bindings. There is no screen for that yet, so it is done with the studio\'s API.',
      do: async () => {
        const set = (await d.api(CHECKER).get(`/api/tenants/${BANK}/configuration`)).data.configurationSet;
        const r = await d.api(CHECKER).post('/api/configuration-releases', { actor: CHECKER, tenant: BANK, bank: BANK, release: `${set.release}+bindings.1.0.0`, bindings: laptopBindings() });
        if (r.status !== 200) throw new Error(`configuration release: ${JSON.stringify(r.data)}`);
        state.R1 = r.data;
        await term(`curl -s -X POST http://localhost:4317/api/configuration-releases -d '{"bank":"${BANK}","bindings": <deploy/laptop/banks/${BANK}/file-transfer.yaml, events.yaml>}'   # as ${CHECKER}`,
          JSON.stringify({ releaseId: state.R1.releaseId, tag: state.R1.tag, files: state.R1.files, bindings: laptopBindings().map((b: any) => `${b.port} -> ${b.adapter}`) }, null, 2));
      },
    },
    {
      say: 'The engineer installs it, and asks for it to be put in force. The runtime checks every value against the bounds the distribution declares. The bank approver consents once, and the release is in force.',
      do: async () => {
        await close();
        await svcAs('service engineer', 'content');
        await d.point(versionRow(state.R1.tag), 1200);
        await confirm(versionRow(state.R1.tag).getByRole('button', { name: 'Install' }), `CR-DEMO-002: put ${state.R1.releaseId} in force (synthetic)`);
        await consent('Agreed: CR-DEMO-002 (synthetic)');
        await svcAs('service engineer', 'content');
        await d.point(releaseRow(state.R1.releaseId), 2500);
      },
    },
    {
      say: 'Now a quick check on the test bench. The tester picks a profile, a salary file on the default engine, then the bank, and a synthetic salary file from the library.',
      do: async () => {
        await runtimeSignIn(d, URLS.testbench, 'tester', 'testbench');
        await d.select(p().getByTestId('profile'), 'wps-default-engine');
        await d.wait(800);
        // The profile names the example tenant; a tester of this runtime acts for its bank only.
        await d.select(p().locator('fieldset', { hasText: 'Tenant' }).locator('select'), BANK);
        const sample = p().getByTestId('sample');
        const value = await sample.locator('option').evaluateAll((os: any[]) => os.find((o) => /wps/i.test(o.value) && /expect settled/i.test(o.textContent))?.value ?? os.find((o) => /wps/i.test(o.value))?.value);
        if (!value) throw new Error('The library has no WPS sample');
        await d.select(sample, value);
      },
    },
    {
      say: 'On this laptop the bench\'s run on Temporal does not yet receive the scheme simulator\'s answer, which is issue I 331. So we untick Temporal, and run the file on the reference interpreter, with the bank\'s values.',
      do: async () => {
        const temporal = p().getByTestId('engine-temporal');
        await d.point(temporal, 1200);
        await d.click(temporal);
        await d.click(p().getByTestId('run'));
        const status = p().getByTestId('status');
        await status.waitFor({ timeout: 60_000 });
        await d.skip('The bench runs', async () => {
          for (let i = 0; i < 600 && !/passed|failed/.test((await status.textContent()) ?? ''); i++) await p().waitForTimeout(1000);
        }, 6);
        const t = (await status.textContent()) ?? '';
        if (!/passed/.test(t)) throw new Error(`The bench run ${t}: ${await p().getByTestId('result').textContent()}`);
      },
    },
    {
      say: 'Passed: the file settles with the bank\'s values. Now one real payment, as the bank would make it.',
      do: async () => { await d.point(p().getByTestId('status'), 1500); await d.scroll(300); },
    },
    {
      say: 'An operator opens the operations console\'s upload page, picks the salary file message, and uploads a synthetic salary file. The File API checks it, keeps it, and starts its payment on the active distribution.',
      do: async () => { state.sif = salaryFile(); state.P1 = await upload(/uae\.wps\.sif/, state.sif.name, state.sif.text); },
    },
    {
      say: 'The payment writes the salary file for the Central Bank, the SIF, and the File API delivers it to the Central Bank\'s folder on the SFTP server. Here it is.',
      do: async () => {
        await d.skip('The payment runs to the Central Bank', async () => {
          await until('the SIF on the SFTP server', async () => ls(CB_SIF).some((n) => n.toUpperCase() === `${state.sif!.stem}.SIF`), 10 * 60_000);
        }, 6);
        state.sent = ls(CB_SIF).find((n) => n.toUpperCase() === `${state.sif!.stem}.SIF`);
        await term('sftp -P 12222 th@127.0.0.1   # ls upload/cb/wps/sif', sh(`${C} exec -T sftp ls -l ${CB_SIF}`));
      },
    },
    {
      say: 'Now we play the Central Bank, and leave an acknowledgement, an ACK, in its answer folder. The File API reads it, matches it to the payment, and the payment goes on.',
      do: async () => {
        const ack = `${state.sif!.stem}S26000000001.ACK`;
        const text = `AHR,ACCEPTED,${state.sent}\nATR,ACCEPTED,2\n`;
        const out = sh(`${C} exec -T -u 1001 sftp sh -c 'mkdir -p ${CB_ACK} ${SFTP_HOME}/cb/wps/writing && cat > ${SFTP_HOME}/cb/wps/writing/${ack} && mv ${SFTP_HOME}/cb/wps/writing/${ack} ${CB_ACK}/${ack} && ls -l ${CB_ACK}' <<'EOF'\n${text}EOF`);
        await term(`sftp -P 12222 th@127.0.0.1   # put ${ack} upload/cb/wps/ack/`, `${text}\n$ ls -l upload/cb/wps/ack\n${out}`);
        await d.skip('The payment reads the ACK and runs to its end', async () => { await until('the payment ended', async () => !!(await pinOf(state.P1!))?.ended); }, 6);
        const ended = (await pinOf(state.P1!)).ended;
        if (ended !== 'settled') throw new Error(`The payment ended ${ended}, not settled`);
      },
    },
    {
      say: 'Settled. Its page in the operations console shows each step as a fact on its timeline. The stack works, end to end.',
      do: async () => {
        await close();
        await runtimeSignIn(d, URLS.ops, 'operator', 'payments');
        const row = p().locator('[data-testid="payment-row"]', { hasText: 'uae.wps' }).first();
        for (let i = 0; i < 60 && !(await row.count()); i++) { await p().waitForTimeout(1000); await p().goto(`${URLS.ops}/`); await runtimeGo(d, URLS.ops, 'payments'); }
        await d.click(row.locator('a').first());
        await d.wait(1200);
        const tl = p().getByRole('tab', { name: /^Timeline/ });
        if (await tl.count()) await d.click(tl.first());
        await d.scroll(400);
      },
    },
    // ================================================================== 5. stop, reset, PostgreSQL
    {
      card: ['5. Stop, reset, or move to PostgreSQL'],
      say: 'Last, how to stop the stack, reset it, and run it on PostgreSQL.',
    },
    {
      say: 'Stop keeps everything: start it again, and it carries on where it was. Down removes the containers, but keeps the volumes, so the data stays too.',
      do: async () => { await term(`${C} stop`, reset ? compose('stop 2>&1 | tail -8') : '(left running)'); },
    },
    {
      say: 'Down, dash v, also removes the volumes: the store, the keys and the files. That is a full reset. The next start is a first start again, with new keys.',
      do: async () => { await term(`${C} down -v`, reset ? compose('down -v 2>&1 | tail -12') : '(left running)'); },
    },
    {
      say: 'To run on PostgreSQL, add the PostgreSQL file to the command. The store, the read model and Temporal then run on one PostgreSQL server, and SQL Server is not started. The same secrets are used, and the SFTP server\'s keys are pinned again.',
      do: async () => {
        if (!reset) { await term(`${PG} up -d --wait`, '(not run)'); return; }
        await d.skip('PostgreSQL starts', async () => {
          sh(`${PG} up -d --wait sftp`);
          process.env.TH_LAPTOP_SFTP_HOST_KEY = hostKey();
          const out = sh(`${PG} up -d --wait --wait-timeout 400 2>&1 | tail -14`);
          await term(`${PG} up -d --wait`, out);
        }, 6);
      },
    },
    {
      say: 'Here it is, healthy, with postgres in place of SQL Server. Everything you saw in this video works the same way on it.',
      do: async () => { await term(`${PG} ps`, reset ? sh(`${PG} ps --format 'table {{.Service}}\\t{{.Status}}' | head -24`) : '(not run)'); await d.wait(1500); await close(); },
    },
    {
      card: ['That is the installation', 'Next: the architecture behind it, and a full payment run.'],
      say: 'That is the installation. The stack is up, the bank\'s first distribution and configuration are in force, and a payment has run. Next, we look at the architecture behind it, and follow a payment end to end.',
    },
  ];
}
