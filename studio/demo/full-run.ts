import * as fs from 'node:fs';
import * as path from 'node:path';
import { toYaml } from '../src/core/yaml.js';
import { ACK_IN, FILE_IN, FILE_IN_SCRIPT, FILE_OUT, GARBAGE, LEG, PORT, RULE, RULESET, SCHEME, SEND, SIM, SUBFLOW, SUBMIT, USD, VALID, VALIDATE } from '../test/blackbox/demo-scheme.js';
import * as D from './hands-on/docs.js';
import { SLOW, type Director, type Scene } from './record.js';
import { BANK, ENVIRONMENT, ROOT, URLS, call, mask, runtimeGo, runtimeSignIn, sh, stackUp, useStackStudio } from './stack.js';

// Runtime video 3: one master workflow, end to end, across every console, on the laptop stack (deploy/laptop).
//
//   The studio: a bank's values, hook and rule on the pre-deployed corporate payment workflow; a new scheme leg (the synthetic
//   uae.demo, as demo/build.ts builds it) connected to that workflow by its dispatch table; the bench; maker-checker; the
//   distribution the approval builds. The service console: install, a bank approver's consent, activate. A payment in through
//   the File API (a WPS file: a bank cannot yet license a scheme built in the studio, I-205), followed in the operations console. A value changed by a configuration release (no deployment). A second
//   distribution activated, the first draining while a payment pinned to it finishes there, then removed; and a roll back.
//
// Where a step has no screen today it is done as an operator would, by the API or a command shown in the terminal panel, and the
// voice says so; each such gap is an Issue in docs/ISSUES.md (I-200 onwards). A WPS payment on Temporal waits in real time for
// the simulator's acknowledgement (I-156 (5)): that wait is cut from the video (Director.skip).
//
// The stack must be up with a compose project of its own and its secrets in the environment, fresh: the production adds the
// bank's studio users, publishes the bank's distributions and installs them.

export const NAME = 'runtime-3-full-run';
export const TITLE = 'TransactHub runtime, part 3: one payment end to end, across every console';
export const PACE = SLOW;

const MAKER = `${BANK}.maker`;
const CHECKER = `${BANK}.checker`;
const SVC = `${URLS.service}/service/api`;
const PLATFORM: [string, string][] = [
  ['p.port.core-banking.v1', 'p.sim.core-banking.v1'], ['p.port.sanctions.v1', 'p.sim.sanctions.v1'], ['p.port.fraud.v1', 'p.sim.fraud.v1'],
];
const DEMO: [string, string][] = [[PORT.name, SIM.name], ['uae.p.port.calendar.v1', 'uae.p.sim.calendar.v1'], ['p.port.encryption.v1', 'p.sim.encryption.v1']];
/** The simulators the front door's one-off run binds: the platform's, WPS's, the demo scheme's and the bank's. */
const RUN_BENCH = [
  ...['core-banking', 'sanctions', 'fraud', 'channel', 'payment-record', 'facts-out', 'encryption'].map((x) => ({ port: `p.port.${x}.v1`, simulator: `p.sim.${x}.v1`, scenario: 'accept' })),
  { port: 'p.port.operations-queue.v1', simulator: 'p.sim.operations.v1', scenario: 'approve' },
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.wps.port.labour-ministry.v1', simulator: 'uae.wps.sim.labour-ministry.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
  { port: PORT.name, simulator: SIM.name, scenario: 'accept' },
  { port: 'b.port.aml-screening.v1', simulator: 'b.sim.aml-screening.v1', scenario: 'accept' },
  { port: 'b.port.labour-ministry.v1', simulator: 'b.sim.labour-ministry.v1', scenario: 'accept' },
];

export async function open(d: Director) {
  if (!(await stackUp())) throw new Error('The laptop stack does not answer: bring it up first (deploy/laptop/README.md)');
  useStackStudio(d);
  await d.page.context().clearCookies();
  // Before the camera: the bank's two studio users, added by one administrator and approved by another (as the laptop test does).
  const admin = d.api('users.admin');
  const users = (await admin.get('/api/users?actor=users.admin')).data as any[];
  if (!users.some((u) => u.id === MAKER)) {
    const wanted = [...users, { id: MAKER, tenant: BANK, roles: ['bank configurator'] }, { id: CHECKER, tenant: BANK, roles: ['bank approver'] }];
    const prop = await admin.post('/api/users/proposals', { actor: 'users.admin', users: wanted });
    await d.api('users.approver').post(`/api/users/proposals/${prop.data.changeSet}/approve`, { actor: 'users.approver' });
  }
  // The studio builds the bank's distributions for Temporal, with simulators, for this test environment.
  await d.api('shared.checker').put('/api/git', { actor: 'shared.checker', target: 'temporal', distributions: true, testDistributions: true });
  await d.as('', 'shared.designer');
  await d.page.goto(`${URLS.studio}/#/catalogue`);
}

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');
  const term = (command: string, output: string) => d.terminal(command, output);
  /** A studio page, as the user signed in (Director.go would sign in as the example bank's maker for the bank's pages). */
  const go = async (hash: string) => {
    await p().goto(`${URLS.studio}/#/${hash}`);
    await p().waitForLoadState('networkidle');
    await d.wait(d.pace.after);
  };
  const close = () => d.closeTerminal();
  const status = async (re: RegExp, within = p()) => {
    for (let i = 0; i < 240; i++) {
      if ((await within.locator('[role="status"]').allTextContents()).some((t: string) => re.test(t))) return;
      await p().waitForTimeout(250);
    }
    throw new Error(`No status matching ${re}`);
  };
  // ------------------------------------------------------------ studio helpers (as demo/build.ts and demo/hands-on.ts)
  const editor = async (kind: string, doc: any) => {
    await go(`editors?kind=${kind}`);
    await d.click(p().getByRole('button', { name: 'Start new' }));
    await d.click(p().getByRole('button', { name: 'Edit the YAML' }).first());
    await d.paste(p().getByLabel('Document YAML').first(), toYaml(doc));
    await d.click(p().getByRole('button', { name: 'Done' }).first());
  };
  const save = async () => { await d.click(p().getByRole('button', { name: /^Save draft$/ }).first()); await status(/Saved draft/); };
  const publish = async (checker = 'shared.checker', maker = d.user) => { await d.release(checker, maker); await d.wait(700); };
  const designer = async (opts: { kind?: 'workflow' | 'hook'; wfKind?: string }, doc: any) => {
    await go('designer');
    await d.select(p().getByLabel('Start from'), { label: 'Blank' });
    if (opts.kind) await d.select(p().getByLabel('Kind', { exact: true }), opts.kind);
    if (opts.wfKind) await d.select(p().getByLabel('Workflow kind'), opts.wfKind);
    await d.type(p().getByLabel('Name', { exact: true }), doc.name, 25);
    await d.type(p().getByLabel('Title', { exact: true }), doc.title, 15);
    await d.click(p().getByRole('button', { name: 'Create' }));
    for (let i = 0; i < 80 && !(await p().getByLabel('Workflow YAML').inputValue()).includes(doc.name); i++) await p().waitForTimeout(100);
    await d.paste(p().getByLabel('Workflow YAML'), toYaml(doc));
    await d.wait(900);
  };
  const bench = async (target: string, tenant: string, ports: [string, string, string?][]) => {
    await go('tests');
    await d.click(p().getByRole('button', { name: 'Ad hoc bench' }));
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
    await d.click(p().getByRole('button', { name: 'Inbound sample' }));
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
    // The set is validated on every edit; the save is offered once no error is left.
    const saveButton = p().getByRole('button', { name: 'Save the configuration set' });
    for (let i = 0; i < 300 && !(await saveButton.isEnabled()); i++) await p().waitForTimeout(100);
    await d.wait(1200);
    await d.click(saveButton);
    await status(/^Saved /);
    await d.wait(1200);
  };
  const approveBank = async (match: string) => {
    await d.as('', CHECKER);
    await go('governance');
    await d.approveRow(match);
  };
  const setValue = (c: any, parameter: string, value: unknown) => {
    const v = c.values.find((x: any) => x.parameter === parameter);
    if (v) { v.value = value; v.validFrom = '2026-01-01'; } else c.values.push({ parameter, value, validFrom: '2026-01-01' });
  };

  // ------------------------------------------------------------ runtime helpers
  const svcAs = async (who: 'service engineer' | 'bank approver', page: string) => {
    if (seen.svc !== who) { await runtimeSignIn(d, URLS.service, who); seen.svc = who; }
    await runtimeGo(d, URLS.service, `service/${page}`);
  };
  const seen: Record<string, string> = {};
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
  const distributions = async () => ((await call(SVC, 'svc.engineer', 'GET', '/content/versions')).data as any[]).filter((v) => v.kind === 'distribution' && v.bank === BANK);
  const configurations = async () => ((await call(SVC, 'svc.engineer', 'GET', '/content/versions')).data as any[]).filter((v) => v.kind === 'configuration' && v.bank === BANK);
  const state: { A?: any; B?: any; C?: any; P1?: string; P2?: string; running?: Promise<string> } = {};

  /** The front door's one-off run of a payment pinned to a distribution (deploy/laptop/bin/run-pinned.mts), in the test bench's container. */
  const runPinned = (paymentId: string, distribution: string, sample: string, message: string) => {
    const req = { paymentId, distribution, bank: BANK, environment: ENVIRONMENT, date: '2026-11-24', sample, message, bench: RUN_BENCH };
    const cmd = `docker compose -f deploy/laptop/compose.yaml exec -T testbench node /opt/th/deploy/laptop/bin/runtime.mjs run-pinned '${JSON.stringify(req)}'`;
    return { shown: `docker compose ... exec testbench node /opt/th/deploy/laptop/bin/runtime.mjs run-pinned '{"paymentId":"${paymentId}","distribution":"${distribution}","sample":"${sample}",...}'`, run: () => new Promise<string>((resolve) => { setTimeout(() => resolve(sh(cmd, { timeout: 1_800_000 })), 10); }) };
  };
  const pin = async (paymentId: string, scheme: string) => (await call(URLS.release, 'th-front-door', 'POST', '/payments/pin', { paymentId, bank: BANK, environment: ENVIRONMENT, scheme })).data;
  const today = new Date().toISOString().slice(0, 10).replace(/-/g, '');

  return [
    // ================================================================== 0
    {
      card: ['Part 3: one payment, end to end', 'From the studio to the service console, the File API and the operations console.'],
      say: 'In this video we follow one piece of work all the way through TransactHub, on the laptop stack from part 1. We configure a platform workflow for the bank, build a new scheme leg and connect it, test it, approve it, install it in the runtime, send a payment in, and follow it. Then we change a value without a deployment, publish a second version, drain and remove the first, and roll back. Everything is synthetic.',
    },
    // ================================================================== 1. a new scheme leg
    {
      card: ['1. A new scheme leg', 'The synthetic scheme uae.demo, built by the platform\'s designer'],
      say: 'We start as the platform\'s workflow designer. The corporate payment workflow is already deployed for the bank. It has a slot, the scheme leg, and a scheme fills it. We will add a new, made-up scheme called U A E demo, so the bank can pay through it.',
      do: async () => { await go('artefact/p.wf.corporate.v1'); await d.scroll(400); },
    },
    {
      say: 'First, its test data: three synthetic files in the bench\'s library. A good file, a file in dollars, and a file that is not a demo file at all.',
      do: async () => {
        await go('tests');
        await d.click(p().getByRole('button', { name: 'Ad hoc bench' }));
        await d.click(p().getByRole('button', { name: 'Inbound sample' }));
        for (const [name, content] of [['demo-credit-file-valid.csv', VALID], ['demo-credit-file-usd.csv', USD], ['demo-credit-file-garbage.csv', GARBAGE]]) {
          await d.click(p().getByRole('button', { name: 'Add a sample file' }));
          await d.type(p().getByLabel('Sample file name'), name, 15);
          await d.paste(p().getByLabel('Sample content'), content);
          await d.click(p().getByRole('button', { name: /^Save the sample as / }));
        }
      },
    },
    {
      say: 'Then the port to the demo clearing house, and its simulator, which stands in for it in tests. They refer to each other, so one change set holds both. A checker, never the maker, approves it.',
      do: async () => { await editor('port', PORT); await save(); await editor('simulator', SIM); await save(); await publish(); },
    },
    {
      say: 'The checks: a rule that a demo file is in dirhams, its rule set, and the activity that runs them. Each is approved by the checker.',
      do: async () => {
        await editor('rule', RULE); await save(); await publish();
        await editor('ruleset', RULESET); await save(); await publish();
        await editor('activity', VALIDATE); await save(); await publish();
      },
    },
    {
      say: 'The maps: the intake map that reads the file into the ISO 20022 context, with its script; the map that writes the file for the clearing house; and the map that reads its acknowledgement. Then the send.',
      do: async () => {
        await editor('map', FILE_IN); await save();
        await d.paste(p().getByLabel('Script source'), FILE_IN_SCRIPT);
        await d.click(p().getByRole('button', { name: /^Save the script as / }));
        await status(/Saved uae\.demo\.map\.file-in\.v1\.ts/);
        await publish();
        await editor('map', FILE_OUT); await save(); await publish();
        await editor('map', ACK_IN); await save(); await publish();
        await editor('activity', SEND); await save(); await publish();
      },
    },
    {
      say: 'Submitting the file and waiting for the answer is one composite step, with a subflow underneath. Then the leg itself: check the file, check each account, write the file, submit it.',
      do: async () => {
        await editor('composite', SUBMIT); await save();
        await designer({ wfKind: 'subflow' }, SUBFLOW); await save();
        await designer({ wfKind: 'leg' }, LEG); await save();
        await d.point(main().locator('.cv-steps').first(), 2000);
      },
    },
    {
      say: 'This is how the leg is connected to the platform workflow: the scheme\'s dispatch table. A credit file starts an outward corporate payment, read by our intake map, with our leg in the scheme leg slot.',
      do: async () => { await editor('scheme', SCHEME); await save(); await main().getByText('Scheme and dispatch table').last().scrollIntoViewIfNeeded(); await d.wait(1500); },
    },
    {
      say: 'The scheme, the composite, the subflow and the leg refer to one another, so the checker approves them as one change set.',
      do: async () => { await publish(); },
    },
    {
      say: 'Test it on the bench: the leg alone, against the clearing house\'s simulator. The file is checked, both accounts pass, the file is submitted, acknowledged, and the leg settles.',
      do: async () => {
        await bench(LEG.name, 'owner', DEMO);
        await withSample('samples/demo-credit-file-valid.csv');
        await runBench();
      },
    },
    // ================================================================== 2. the bank's values, hook and rule
    {
      card: ['2. The bank\'s own values, hook and rule', 'As the bank\'s configurator, on the pre-deployed corporate workflow'],
      say: 'Now we work for the bank, as its configurator. The bank cannot change the shared workflow, but it can take a new scheme, set values within their bounds, add a rule of its own, and plug a hook into a hook point.',
      do: async () => { await d.as('', MAKER); await go('bank'); },
    },
    {
      say: 'First the bank takes the new scheme: it accepts U A E demo and enables its rail, in its configuration set. The checks run as we type. The bank\'s approver, another person, approves it. That builds a configuration release: values, never code.',
      do: async () => {
        await configuration((c) => {
          if (!c.accepts.some((a: any) => a.scope === 'uae.demo')) c.accepts.push({ scope: 'uae.demo', release: '1.0.0' });
          const rails = c.values.find((v: any) => v.parameter === 'p.table.rail-choice.v1#enabledRails');
          if (rails && !rails.value.includes('uae.demo')) rails.value.push('uae.demo');
        });
        await approveBank(`config:${BANK}`);
        await d.as('', MAKER);
      },
    },
    {
      say: 'The bank\'s rule: a file with more lines than the bank allows needs a person\'s approval. Its limit, max lines, is a parameter with a default and bounds. A rule set and a check activity run it.',
      do: async () => {
        await editor('rule', D.BANK_RULE); await save();
        await editor('ruleset', D.BANK_RULESET); await save();
        await editor('activity', D.BANK_ACTIVITY); await save();
      },
    },
    {
      say: 'The hook runs that check after a payment is validated, at a hook point of the platform\'s check and screen segment. If the check rejects, the hook ends as hold, and the payment waits for a person.',
      do: async () => { await designer({ kind: 'hook' }, D.BANK_HOOK); await save(); await d.point(main().locator('.cv-steps').first(), 2000); },
    },
    {
      say: 'The four refer to one another, so the bank\'s checker approves them as one change set. A change to the bank\'s pack is logic, so this approval builds the bank\'s distribution: a signed, complete copy of everything the bank runs, now with the new scheme and the bank\'s hook, published to the GitHub branch with a tag.',
      do: async () => { await publish(CHECKER, MAKER); },
    },
    {
      say: 'Then the values: bind the hook to its hook point, and set max lines to five. Approved by the bank\'s approver, this is a second configuration release.',
      do: async () => {
        await configuration((c) => {
          setValue(c, `${D.BANK_RULE.name}#maxLines`, 5);
          if (!c.bindings.some((b: any) => b.hook === D.BANK_HOOK.name)) c.bindings.push({ hookPoint: 'p.seg.check-and-screen.v1#after-validate', hook: D.BANK_HOOK.name, release: '1.0.0', validFrom: '2026-01-01' });
        });
        await approveBank(`config:${BANK}`);
        await d.as('', MAKER);
      },
    },
    {
      say: 'A whole payment for the bank on the studio\'s bench: a demo credit file starts the corporate workflow, the bank\'s hook passes it, two lines under the limit of five, the rail is our new scheme, and our leg runs in the slot.',
      do: async () => {
        await bench('p.wf.corporate.v1', BANK, [...PLATFORM, ...DEMO]);
        await withSample('samples/demo-credit-file-valid.csv', 'uae.demo.credit-file');
        await runBench();
      },
    },
    {
      say: 'Export shows what went to the content branch: each approved change set as one commit, with the bank\'s distribution and configuration releases under their tags.',
      do: async () => { await go('export'); await d.scroll(500); await d.scroll(400); },
    },
    // ================================================================== 3. install
    {
      card: ['3. Install, consent, activate', 'In the service console'],
      say: 'Now the runtime. A service engineer opens the service console. The new distribution is offered on GitHub. Verify checks its signature against the trusted signers. Difference shows what changes against what is active.',
      do: async () => {
        await svcAs('service engineer', 'content');
        const dist = (await distributions()).filter((v) => v.state === 'offered').pop();
        state.A = dist;
        if (dist) {
          await d.point(versionRow(dist.tag), 1500);
          await d.click(versionRow(dist.tag).getByRole('button', { name: 'Difference' }));
        }
      },
    },
    {
      say: 'Install asks for a reason, and then waits. The engineer who asked may not consent: a bank approver, another person, must.',
      do: async () => {
        if (!state.A) return;
        await confirm(versionRow(state.A.tag).getByRole('button', { name: 'Install' }), `CR-DEMO-001: install ${state.A.release} (synthetic)`);
      },
    },
    {
      say: 'The bank approver signs in, sees what waits, and approves it with a reason. The distribution is stored, insert-only.',
      do: async () => { await consent('Agreed: CR-DEMO-001 (synthetic)'); },
    },
    {
      say: 'The engineer activates it, and the approver consents again. From now on, new payments of this bank take this distribution.',
      do: async () => {
        await svcAs('service engineer', 'content');
        await confirm(releaseRow(state.A.releaseId).getByRole('button', { name: 'Activate' }), `CR-DEMO-001: activate ${state.A.release} (synthetic)`);
        await consent('Agreed: activate (synthetic)');
        await svcAs('service engineer', 'content');
        await d.point(releaseRow(state.A.releaseId), 2000);
      },
    },
    {
      say: 'The bank\'s values come the same way: the configuration releases are installed, each with the approver\'s consent, and checked against the distribution\'s parameter contract.',
      do: async () => {
        for (const c of (await configurations()).filter((v) => v.state === 'offered')) {
          await svcAs('service engineer', 'content');
          await confirm(versionRow(c.tag).getByRole('button', { name: 'Install' }), `CR-DEMO-001: values ${c.release} (synthetic)`);
          await consent('Agreed: values (synthetic)');
        }
        await svcAs('service engineer', 'content');
        await d.point(p().getByRole('table', { name: 'Releases' }), 2000);
      },
    },
    // ================================================================== 4. a payment in
    {
      card: ['4. A payment in, through the File API', 'The only door for files'],
      say: 'Now a payment. One gap first: a bank licenses a scheme in its bank pack\'s lock file, and the studio cannot change that file today. So this distribution does not carry U A E demo yet, and a demo file would find no route. We send a WPS salary file instead. That gap is recorded as an issue.',
      do: async () => { await term('cat content-configurations/banks/adib/pack.yaml | grep -A2 licensedSchemes', sh('grep -A2 licensedSchemes content-configurations/banks/adib/pack.yaml')); },
    },
    {
      say: 'Every file comes in through the File API. A channel uploads a file over HTTPS, or the SFTP gateway picks it up from an inbox. There is no upload screen in the consoles yet, so we send it as a channel would, with its API.',
      do: async () => {
        const sif = fs.readFileSync(path.join(ROOT, 'content-configurations', 'samples', 'wps-sif-valid.sif'));
        const body = { tenant: BANK, message: 'uae.wps.sif', fileName: `${today}0001.SIF`, contentBase64: sif.toString('base64') };
        const r = await call(URLS.files, 'ops.operator', 'POST', '/files/ui', body);
        await term(`curl -s -X POST http://localhost:8485/files/ui -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' \\\n  -d '{"tenant":"${BANK}","message":"uae.wps.sif","fileName":"${today}0001.SIF","contentBase64":"..."}'`, mask(JSON.stringify(r.data, null, 2)));
      },
    },
    {
      say: 'The File API checks the file, keeps it with its hash, and hands it to the front door. On this laptop the front door has no worker to run the scheme\'s intake map, so it refuses to start the payment, and says why. That is a gap in the laptop stack, recorded as an issue.',
      do: async () => { await d.wait(2500); },
    },
    {
      say: 'So we do what the laptop\'s own test does: the front door pins the payment to the active distribution, and its one-off runner runs it from that copy, on Temporal, on SQL Server.',
      do: async () => {
        state.P1 = `PAY-DEMO-${today}-1`;
        const pinned = await pin(state.P1, 'uae.wps');
        const r = runPinned(state.P1, pinned.distribution ?? state.A.releaseId, 'content-configurations/samples/wps-sif-valid.sif', 'uae.wps.sif');
        await term(`curl -s -X POST http://localhost:8481/payments/pin ... '{"paymentId":"${state.P1}","bank":"${BANK}","scheme":"uae.wps"}'\n${JSON.stringify(pinned)}\n\n$ ${r.shown}`, '(running)');
        state.running = r.run();
      },
    },
    // ================================================================== 5. follow it
    {
      card: ['5. Follow it in the operations console', 'As the bank\'s operator'],
      say: 'An operator follows the payment in the operations console. The read model is up to date. Here is our payment, with its scheme, its amount and its legs.',
      do: async () => {
        await close();
        await runtimeSignIn(d, URLS.ops, 'operator', 'payments');
        await d.wait(1500);
      },
    },
    {
      say: 'Its page: the legs, and the timeline of facts, one per step, each chained to the one before. The bank\'s hook ran after validation and passed the file: one line, under the limit of five.',
      do: async () => {
        const row = p().locator('[data-testid="payment-row"]').first();
        if (await row.count()) await d.click(row.locator('a').first());
        for (const t of [/^Legs/, /^Timeline/]) { const tab = p().getByRole('tab', { name: t }); if (await tab.count()) await d.click(tab.first()); }
        await d.scroll(400);
      },
    },
    {
      say: 'We let it run to its end.',
      do: async () => {
        await d.skip('The payment runs to its end', async () => { const out = (await state.running) ?? ""; await term('run-pinned', lastJson(out)); });
        await runtimeGo(d, URLS.ops, `payments/${encodeURIComponent(state.P1 ?? '')}`);
      },
    },
    // ================================================================== 6. hot update
    {
      card: ['6. Change a value, with no deployment', 'A configuration release'],
      say: 'The bank now wants a lower limit: three lines. A value is not code, so no new distribution is needed. The configurator changes max lines, and the approver approves it.',
      do: async () => {
        useStackStudio(d);
        await d.page.context().clearCookies();
        await d.as('', MAKER);
        await configuration((c) => setValue(c, `${D.BANK_RULE.name}#maxLines`, 3));
        await approveBank(`config:${BANK}`);
      },
    },
    {
      say: 'That approval builds a configuration release: signed, dated values, checked against the parameter contract of every distribution the bank still runs. The service engineer installs it, and the approver consents. Nothing is redeployed.',
      do: async () => {
        seen.svc = '';
        await svcAs('service engineer', 'content');
        const c = (await configurations()).filter((v) => v.state === 'offered').pop();
        state.C = c;
        if (c) {
          await d.point(versionRow(c.tag), 1500);
          await confirm(versionRow(c.tag).getByRole('button', { name: 'Install' }), `CR-DEMO-002: max lines 3 (synthetic)`);
          await consent('Agreed: CR-DEMO-002 (synthetic)');
          await svcAs('service engineer', 'content');
        }
      },
    },
    {
      say: 'Values are looked up by date. A rule that reads its value as of the step sees the new one from its date, even in a payment already running. The distribution, the code, did not change.',
      do: async () => { await d.scroll(400); },
    },
    // ================================================================== 7. a second distribution
    {
      card: ['7. A second distribution, side by side', 'Activate B; A drains; a payment pinned to A finishes on A'],
      say: 'Now a change of logic: the bank changes its hook. Logic travels only in a distribution. The configurator saves the hook\'s new release, and the approver approves it, which builds distribution B.',
      do: async () => {
        useStackStudio(d);
        await d.page.context().clearCookies();
        await d.as('', MAKER);
        // A new release of the published hook, opened in the designer (?open=<name>), its title changed.
        await go(`designer?open=${encodeURIComponent(D.BANK_HOOK.name)}`);
        for (let i = 0; i < 80 && !(await p().getByLabel('Workflow YAML').inputValue()).includes(D.BANK_HOOK.name); i++) await p().waitForTimeout(100);
        const yaml = await p().getByLabel('Workflow YAML').inputValue();
        await d.paste(p().getByLabel('Workflow YAML'), yaml.replace(/^title: .*$/m, 'title: Hold large salary and credit files for a person to approve'));
        await d.wait(900);
        await save();
        await publish(CHECKER, MAKER);
      },
    },
    {
      say: 'Before B is active, a second payment arrives. It is pinned to A, and starts on A.',
      do: async () => {
        state.P2 = `PAY-DEMO-${today}-2`;
        const pinned = await pin(state.P2, 'uae.wps');
        const r = runPinned(state.P2, pinned.distribution ?? state.A.releaseId, 'content-configurations/samples/wps-sif-valid.sif', 'uae.wps.sif');
        await term(`curl -s -X POST http://localhost:8481/payments/pin ... '{"paymentId":"${state.P2}","scheme":"uae.wps"}'`, `${JSON.stringify(pinned)}\n\n$ ${r.shown}\n(running: a WPS payment waits for the scheme's acknowledgement)`);
        state.running = r.run();
      },
    },
    {
      say: 'The engineer installs B and activates it, each with the approver\'s consent. Activating B moves A to draining in the same step. A still has one open payment.',
      do: async () => {
        await close();
        seen.svc = '';
        await svcAs('service engineer', 'content');
        const dist = (await distributions()).filter((v) => v.state === 'offered').pop();
        state.B = dist;
        if (!dist) return;
        await confirm(versionRow(dist.tag).getByRole('button', { name: 'Install' }), `CR-DEMO-003: install ${dist.release} (synthetic)`);
        await consent('Agreed: CR-DEMO-003 (synthetic)');
        await svcAs('service engineer', 'content');
        await confirm(releaseRow(dist.releaseId).getByRole('button', { name: 'Activate' }), `CR-DEMO-003: activate ${dist.release} (synthetic)`);
        await consent('Agreed: activate B (synthetic)');
        await svcAs('service engineer', 'content');
        await d.point(releaseRow(state.A.releaseId), 2500);
      },
    },
    {
      say: 'Removing A now is refused: a payment pinned to it is open. The button stays off. New payments take B; our payment keeps running on A.',
      do: async () => { await d.hover(releaseRow(state.A.releaseId).getByRole('button', { name: 'Remove' }), 2500); },
    },
    {
      say: 'We wait for it to finish.',
      do: async () => {
        await d.skip('The payment pinned to A runs to its end, on A', async () => { const out = (await state.running) ?? ""; await term('run-pinned', lastJson(out)); });
        await close();
        await svcAs('service engineer', 'content');
        await d.point(releaseRow(state.A.releaseId), 2000);
      },
    },
    {
      say: 'It finished on A: its task queues and its build id are A\'s. A is removed only once no payment pinned to it is open. Here one still counts as open: the file the File API refused at the start was pinned to A, but never became a payment. That is an issue we recorded. When none is open, A is drained, and the engineer removes it with the approver\'s consent; its package stays on GitHub and in the store, as a record.',
      do: async () => {
        const remove = releaseRow(state.A.releaseId).getByRole('button', { name: 'Remove' });
        if (await remove.isEnabled()) {
          await confirm(remove, `CR-DEMO-004: remove ${state.A.release}, drained (synthetic)`);
          await consent('Agreed: CR-DEMO-004 (synthetic)');
          await svcAs('service engineer', 'content');
        } else await d.hover(remove, 2500);
        await d.point(p().getByRole('table', { name: 'Releases' }), 2000);
      },
    },
    // ================================================================== 8. roll back
    {
      card: ['8. Roll back', 'Activate the earlier distribution again'],
      say: 'Last, a roll back. Rolling back is activating the earlier distribution again, with consent: new payments take A again, and payments that started on B finish on B. Had A been removed, it would be installed again from GitHub first.',
      do: async () => {
        const again = (await distributions()).find((v) => v.tag === state.A.tag);
        if (again?.state === 'offered') {
          await confirm(versionRow(state.A.tag).getByRole('button', { name: 'Install' }), `CR-DEMO-005: reinstall ${state.A.release} (synthetic)`);
          await consent('Agreed: CR-DEMO-005 (synthetic)');
          await svcAs('service engineer', 'content');
        }
        const row = releaseRow(state.A.releaseId);
        if (await row.getByRole('button', { name: 'Activate' }).count()) {
          await confirm(row.getByRole('button', { name: 'Activate' }), `CR-DEMO-005: roll back to ${state.A.release} (synthetic)`);
          await consent('Agreed: roll back (synthetic)');
          await svcAs('service engineer', 'content');
        }
        await d.point(p().getByRole('table', { name: 'Releases' }), 2500);
      },
    },
    {
      say: 'Every one of those steps is a new row in the store: who asked, who consented, when, and why. Nothing was updated or deleted. The service console\'s error and event log shows them.',
      do: async () => { await svcAs('service engineer', 'logs'); },
    },
    {
      card: ['That is the whole run', 'Studio, service console, File API, operations console: one bank, end to end.'],
      say: 'That is the whole run: a bank\'s values, hook and rule, and a new scheme leg, built and tested in the studio, approved by a second person, installed with a bank approver\'s consent, paid, followed, changed without a deployment, replaced side by side while the first drained, and rolled back.',
    },
  ];
}

/** The last JSON line a run printed, shortened for the terminal panel. */
function lastJson(out: string): string {
  const line = out.trim().split('\n').filter((l) => l.startsWith('{')).pop();
  if (!line) return out.slice(-1500);
  try {
    const r = JSON.parse(line);
    return JSON.stringify({ paymentId: r.paymentId, outcome: r.outcome, ended: r.ended, minutes: Math.round(r.ms / 600) / 100, distribution: r.distribution?.id, taskQueues: r.distribution?.taskQueues }, null, 2);
  } catch { return line.slice(0, 1500); }
}
