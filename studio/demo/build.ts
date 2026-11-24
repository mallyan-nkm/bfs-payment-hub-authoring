import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { toYaml } from '../src/core/yaml.js';
import { STUDIO } from '../test/blackbox/server.js';
import { ACK_IN, FILE_IN, FILE_IN_SCRIPT, FILE_OUT, GARBAGE, LEG, PORT, RULE, RULESET, SCHEME, SEND, SIM, SUBFLOW, SUBMIT, USD, VALID, VALIDATE } from '../test/blackbox/demo-scheme.js';
import type { Director, Scene } from './record.js';

// Demo 2: a new scheme and its workflow, built from scratch in the web app, approved, enabled for a
// bank, paid end to end, exported and deployed on DTFx. The same steps as the user guide's part 6
// and the browser test test/blackbox/ui-build.test.ts.

export const NAME = '02-build-a-scheme-from-scratch';
export const TITLE = 'TransactHub Designer Studio: building a scheme and its workflow from scratch';

const PLATFORM: [string, string][] = [
  ['p.port.core-banking.v1', 'p.sim.core-banking.v1'],
  ['p.port.sanctions.v1', 'p.sim.sanctions.v1'],
  ['p.port.fraud.v1', 'p.sim.fraud.v1'],
];
const DEMO: [string, string][] = [[PORT.name, SIM.name], ['uae.p.port.calendar.v1', 'uae.p.sim.calendar.v1'], ['p.port.encryption.v1', 'p.sim.encryption.v1']];

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');
  const status = async (re: RegExp) => {
    for (let i = 0; i < 120; i++) {
      if ((await p().locator('[role="status"]').allTextContents()).some((t: string) => re.test(t))) return;
      await p().waitForTimeout(250);
    }
    throw new Error(`No status matching ${re}`);
  };
  const editor = async (kind: string, doc: any) => {
    await d.go(`editors?kind=${kind}`);
    await d.click(p().getByRole('button', { name: 'Start new' }));
    await d.click(p().getByRole('button', { name: 'Edit the YAML' }).first());
    await d.paste(p().getByLabel('Document YAML').first(), toYaml(doc));
    await d.click(p().getByRole('button', { name: 'Done' }).first());
  };
  const save = async () => {
    await d.click(p().getByRole('button', { name: /^Save draft$/ }).first());
    await status(/Saved draft/);
  };
  const publish = async () => {
    await d.release('shared.checker', 'shared.designer');
    await p().waitForTimeout(700);
  };
  const designer = async (wfKind: string, doc: any) => {
    await d.go('designer');
    await d.select(p().getByLabel('Start from'), { label: 'Blank' });
    await d.select(p().getByLabel('Workflow kind'), wfKind);
    await d.type(p().getByLabel('Name', { exact: true }), doc.name, 25);
    await d.type(p().getByLabel('Title', { exact: true }), doc.title, 15);
    await d.click(p().getByRole('button', { name: 'Create' }));
    // The blank starter loads from the server; paste only once it is in, or it overwrites the paste.
    for (let i = 0; i < 80 && !(await p().getByLabel('Workflow YAML').inputValue()).includes(doc.name); i++) await p().waitForTimeout(100);
    await d.paste(p().getByLabel('Workflow YAML'), toYaml(doc));
    await p().waitForTimeout(900);
  };
  const bench = async (target: string, tenant: string, ports: [string, string][], sample: string, message?: string) => {
    await d.forTenant(tenant);
    await d.go('tests');
    await d.click(p().getByRole('tab', { name: 'Ad hoc bench' }));
    await d.select(p().getByLabel('Target', { exact: true }), target);
    await d.select(p().getByLabel('Run tenant'), tenant);
    for (const [port, sim] of ports) {
      await d.click(p().getByRole('button', { name: 'Add a port' }));
      await d.select(p().getByLabel('Port', { exact: true }).last(), port);
      await d.select(p().getByLabel('Simulator', { exact: true }).last(), sim);
      await d.select(p().getByLabel('Scenario', { exact: true }).last(), 'accept');
    }
    await d.click(p().getByRole('tab', { name: 'Inbound sample' }));
    await d.select(p().getByLabel('Inbound sample'), sample);
    if (message) await d.select(p().getByLabel('Start message'), message);
  };
  let zip = '';

  return [
    {
      say: 'In this demo we build a new payment scheme from nothing, and connect it to the corporate payment workflow. The scheme is called U A E demo. It is synthetic, made up for training, and its file is a simple list: a header record, then one record per credit.',
      do: async () => { await d.go('catalogue'); },
    },
    {
      say: 'First, the test data. In the test bench, we save three synthetic sample files: a valid file, a file in dollars, and a file that is not a demo file at all.',
      do: async () => {
        await d.go('tests');
        await d.click(p().getByRole('tab', { name: 'Ad hoc bench' }));
        await d.click(p().getByRole('tab', { name: 'Inbound sample' }));
        for (const [name, content] of [['demo-credit-file-valid.csv', VALID], ['demo-credit-file-usd.csv', USD], ['demo-credit-file-garbage.csv', GARBAGE]]) {
          await d.click(p().getByRole('button', { name: 'Add a sample file' }));
          await d.type(p().getByLabel('Sample file name'), name, 20);
          await d.paste(p().getByLabel('Sample content'), content);
          await d.click(p().getByRole('button', { name: /^Save the sample as / }));
        }
      },
    },
    {
      say: 'Next, the port: the connection to the demo clearing house. Every port needs a simulator that stands in for the party in tests, so we write the port, and save it as a draft.',
      do: async () => { await editor('port', PORT); await save(); },
    },
    {
      say: 'The simulator plays the clearing house. It carries the eight standard scenarios: accept, reject, no reply, a late reply, duplicates, and more. We save it too.',
      do: async () => { await editor('simulator', SIM); await d.scroll(300); await save(); },
    },
    {
      say: 'The port and its simulator refer to each other, so neither can be published first. The studio generates a change set: approving one publishes both, checked and tested together. A checker who is not the maker approves it.',
      do: async () => { await publish(); },
    },
    {
      say: 'Now the rule: a demo file must be in dirhams, otherwise it is rejected with reason code A M zero three. Its two examples run in the gates as we edit.',
      do: async () => { await editor('rule', RULE); await d.scroll(300); await save(); await publish(); },
    },
    {
      say: 'The rule goes into a rule set, and the rule set into the file checks: a validate activity, with the outcomes pass and reject.',
      do: async () => { await editor('ruleset', RULESET); await save(); await publish(); await editor('activity', VALIDATE); await save(); await publish(); },
    },
    {
      say: 'The intake map reads the file into the canonical context. It is bound to a script, so once the draft is saved, a script-behind editor appears.',
      do: async () => { await editor('map', FILE_IN); await save(); await p().getByLabel('Script source').scrollIntoViewIfNeeded(); },
    },
    {
      say: 'The script reads the header and the credits, and writes the batch and its items. It uses only what the studio injects: no clock, no randomness, no network. It does not say what payment the file starts. The dispatch table does that.',
      do: async () => {
        await d.paste(p().getByLabel('Script source'), FILE_IN_SCRIPT);
        await d.click(p().getByRole('button', { name: /^Save the script as / }));
        await status(/Saved uae\.demo\.map\.file-in\.v1\.ts/);
        await publish();
      },
    },
    {
      say: 'Three more blocks: a map that renders the file for the clearing house, a map that reads its acknowledgement, and the send itself, which may be retried, so it carries an idempotency key.',
      do: async () => {
        await editor('map', FILE_OUT); await save(); await publish();
        await editor('map', ACK_IN); await save(); await publish();
        await editor('activity', SEND); await save(); await publish();
      },
    },
    {
      say: 'Submitting the file and waiting for the acknowledgement is one composite step. Its outcomes are accepted, rejected, and pending if no answer comes within a business day.',
      do: async () => { await editor('composite', SUBMIT); await save(); },
    },
    {
      say: 'Underneath, a subflow does the work: send the file, await the acknowledgement, and read it. We build it in the workflow designer.',
      do: async () => { await designer('subflow', SUBFLOW); await save(); },
    },
    {
      say: 'Now the leg: the scheme part of the payment. It sets the business date, checks the file, checks every IBAN, renders the file and submits it. It fills the scheme leg slot of the corporate payment workflow.',
      do: async () => { await designer('leg', LEG); await d.click(p().getByRole('tab', { name: 'Expanded form' })); await p().waitForTimeout(900); await save(); },
    },
    {
      say: 'Last, the scheme and its dispatch table. The credit file starts an outward corporate payment, read by our intake map, with our leg in the slot. The acknowledgement is delivered to the workflow that awaits it.',
      do: async () => { await editor('scheme', SCHEME); await save(); await main().getByText('Scheme and dispatch table').last().scrollIntoViewIfNeeded(); },
    },
    {
      say: 'These four refer to one another, so the change set holds them all: the scheme, the composite, the subflow and the leg. The checker approves them together.',
      do: async () => { await publish(); },
    },
    {
      say: 'Let us run the leg on the bench, against the simulator. The file is checked, both IBANs pass, the file is submitted and acknowledged, and the leg settles.',
      do: async () => {
        await bench(LEG.name, 'owner', DEMO, 'samples/demo-credit-file-valid.csv');
        await d.click(p().getByRole('button', { name: 'Run the bench' }));
        await p().waitForTimeout(1500);
        await main().getByText('Business trace').first().scrollIntoViewIfNeeded();
      },
    },
    {
      say: 'Now we act as the example bank. First it licenses the new scheme: its configurator proposes the next lock file of the bank pack, and its approver, another person, approves it. That builds the bank\'s next distribution, which carries the scheme.',
      do: async () => {
        await d.licenseScheme('uae.demo');
      },
    },
    {
      say: 'A bank chooses which rails it runs. Until it accepts the new scheme and enables its rail, a payment in it is rejected at the route. So we release a new configuration set.',
      do: async () => {
        await d.go('tenant');
        await d.as('example-bank', 'example.maker');
        await d.go('tenant');
        const cs = (await d.api().get('/api/tenants/example-bank/configuration')).data;
        const next = JSON.parse(JSON.stringify(cs.configurationSet));
        // A later release than the shipped one, whatever it is (R-11-35).
        next.release = next.release.replace(/^(\d+)\.(\d+)\..*/, (_: string, M: string, m: string) => `${M}.${Number(m) + 1}.0`);
        next.accepts.push({ scope: 'uae.demo', release: '1.0.0' });
        next.values.find((v: any) => v.parameter === 'p.table.rail-choice.v1#enabledRails').value.push('uae.demo');
        await d.paste(p().getByLabel('Configuration set YAML'), toYaml(next));
        await d.click(p().getByRole('button', { name: 'Save the configuration set' }));
        await p().waitForTimeout(800);
        await d.approveConfiguration();
        await p().waitForTimeout(800);
      },
    },
    {
      say: 'And now a whole payment. The credit file arrives from the bank channel. The dispatch table routes it to the corporate workflow, which screens it, reserves the funds, chooses our rail, runs our leg in its slot, and posts it.',
      do: async () => {
        await bench('p.wf.corporate.v1', 'example-bank', [...PLATFORM, ...DEMO], 'samples/demo-credit-file-valid.csv', 'uae.demo.credit-file');
        await d.click(p().getByRole('button', { name: 'Run the bench' }));
        await p().waitForTimeout(2000);
        await main().getByText('Business trace').first().scrollIntoViewIfNeeded();
      },
    },
    {
      say: 'Time to export. We export the corporate workflow for the bank, for D T F X, with simulators for a test deployment. The new scheme gets a worker of its own, beside the platform, the cluster and the bank.',
      do: async () => {
        await d.go('export?workflows=p.wf.corporate.v1&target=dtfx');
        await d.select(p().getByLabel('Export tenant'), 'example-bank');
        await d.click(p().getByLabel('With simulators'));
        await d.click(p().getByRole('button', { name: 'Preview' }));
        await p().waitForTimeout(1200);
        await d.scroll(350);
        const [download] = await Promise.all([p().waitForEvent('download'), p().getByText('Download the zip').click()]);
        zip = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'th-demo-zip-')), 'corporate.zip');
        await download.saveAs(zip);
      },
    },
    {
      say: 'Finally, we deploy the package on D T F X. Each worker starts as its own process. The front door reads the file, the dispatch table routes it, and the payment settles on a real workflow engine.',
      do: async () => {
        const sample = d.server.contentFile('samples/demo-credit-file-valid.csv');
        const bench = [...PLATFORM, ...DEMO].flatMap(([port, sim]) => ['--bench', `${port}=${sim}:accept`]);
        const args = ['src/cli.ts', 'deploy', zip, '--workflow', 'p.wf.corporate.v1', '--message', 'uae.demo.credit-file', '--inbound', sample, '--tenant', 'example-bank', ...bench];
        const out = await new Promise<string>((resolve) => {
          const c = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), args, { cwd: STUDIO, env: { ...process.env } });
          let o = '';
          c.stdout.on('data', (b) => (o += b));
          c.stderr.on('data', (b) => (o += b));
          c.on('close', () => resolve(o));
        });
        if (!/^outcome settled/m.test(out)) throw new Error(`The deploy did not settle:\n${out.slice(-1500)}`);
        const shown = out.split('\n').filter((l) => /^(configuration|worker .* up|intake|front door|outcome|trace)/.test(l)).join('\n');
        await d.terminal('npm run studio -- deploy corporate.zip --workflow p.wf.corporate.v1 --message uae.demo.credit-file --tenant example-bank ...', shown);
      },
    },
    {
      say: 'That is a scheme built from scratch, routed by its dispatch table into the corporate payment workflow, approved through maker and checker, enabled by a bank, and run on a real engine. The user guide walks through every step.',
      do: async () => { await p().waitForTimeout(1500); await d.closeTerminal(); await d.go('catalogue'); },
    },
  ];
}
