import type { Director, Scene } from './record.js';

// Demo 1: the ideas and a tour of the studio, on the factory content.

export const NAME = '01-concepts-and-walkthrough';
export const TITLE = 'TransactHub Designer Studio: concepts and walkthrough';

const PLATFORM: [string, string][] = [
  ['p.port.core-banking.v1', 'p.sim.core-banking.v1'],
  ['p.port.sanctions.v1', 'p.sim.sanctions.v1'],
  ['p.port.fraud.v1', 'p.sim.fraud.v1'],
  ['uae.wps.port.wps.v1', 'uae.wps.sim.wps-outward.v1'],
  ['uae.p.port.calendar.v1', 'uae.p.sim.calendar.v1'],
];

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');
  return [
    {
      say: 'Welcome to the TransactHub Designer Studio. This is where a payments designer defines, tests and exports payment workflows. It is a design-time tool: it never reads or changes a running payment.',
      do: async () => { await d.go('catalogue'); },
    },
    {
      say: 'Everything you do is done as a user, in a tenant. The owner tenant holds shared content: the platform and the schemes. Each bank is a tenant of its own. Roles are enforced, and whoever makes a change never approves it.',
      do: async () => { await d.hover(p().getByLabel('Signed in as'), 1800); },
    },
    {
      say: 'The catalogue lists every artefact. A name has four parts: a scope, a type, a name, and the major version. Let us search for IBAN.',
      do: async () => { await d.type(p().getByRole('searchbox', { name: 'Search' }), 'iban', 120); },
    },
    {
      say: 'This is an activity: a block that a workflow calls. Its contract says what it reads, what it writes, and the outcomes it can end with. The page also shows which workflows use it, and the tests that target it.',
      do: async () => { await d.click(p().locator('a[href="#/artefact/uae.p.validate.iban.v1"]').first()); await d.scroll(500); await d.scroll(500); },
    },
    {
      say: 'Here is the corporate payment workflow, a platform workflow. It validates, screens, checks limits, reserves funds and routes the payment. It has a slot called scheme leg, which a scheme fills, and hook points, where a bank adds its own logic.',
      do: async () => { await d.go('artefact/p.wf.corporate.v1'); await d.scroll(450); await d.scroll(450); },
    },
    {
      say: 'Every workflow works on one canonical context, modelled on ISO 20022. Fields that hold personal data are flagged. They must never appear in workflow state, logs, tests or samples.',
      do: async () => { await d.go('data?tab=model'); await d.click(p().getByRole('button', { name: 'Open all' })); await d.scroll(350); },
    },
    {
      say: 'Reason codes come from the ISO external code sets, kept by version. A M zero four, for example, is insufficient funds.',
      do: async () => {
        await d.go('data?tab=registry');
        await d.select(p().getByLabel('Code set'), 'ExternalStatusReason1Code');
        await d.type(p().getByLabel('Code', { exact: true }), 'AM04', 150);
        await d.click(p().getByRole('button', { name: 'Look up' }));
      },
    },
    {
      say: 'A scheme has a dispatch table, which routes every message. In the wages protection system, a salary file starts an outward corporate payment. The table names the intake map that reads the file, and the leg that fills the slot.',
      do: async () => { await d.go('data?tab=paths'); await d.click(main().locator('tr', { hasText: 'uae.wps' }).first()); await d.scroll(400); },
    },
    {
      say: 'In the workflow designer, the YAML on the left and the canvas on the right edit the same document. The checks run as you type.',
      do: async () => {
        await d.go('designer');
        await d.select(p().getByLabel('Published workflow'), { label: 'p.wf.corporate.v1 @1.0.0' });
        await d.click(p().getByRole('button', { name: 'Open', exact: true }).last());
        await p().waitForTimeout(800);
      },
    },
    {
      say: 'A step that talks to a party and waits for an answer calls a composite. The expanded form is what an engine runs: each composite step becomes a block of inner steps.',
      do: async () => { await d.click(p().getByRole('tab', { name: 'Expanded form' })); await d.scroll(300); },
    },
    {
      say: 'Everything that is not a workflow has its own editor: rules, rule sets, decision tables, activity contracts, maps, simulators, composites, ports and schemes. Each runs its gates as you edit.',
      do: async () => {
        await d.go('editors?kind=rule');
        for (const tab of ['Rule set', 'Decision table', 'Simulator', 'Scheme and dispatch table']) await d.click(p().getByRole('tab', { name: tab, exact: true }));
      },
    },
    {
      say: 'Tests run on a virtual clock, so a wait of days ends at once. This factory test sends a salary file to a simulator that stands in for the wages protection system.',
      do: async () => {
        await d.go('tests');
        await d.type(p().getByLabel('Filter tests'), 'WPS-OUT-001', 60);
        await d.click(p().locator('tr', { hasText: 'WPS-OUT-001' }).first().getByRole('button', { name: 'Run' }));
        await p().waitForTimeout(1500);
      },
    },
    {
      say: 'The business trace has one line for each step. The detail shows the steps inside the submit composite: send the file, await the acknowledgement, and read it.',
      do: async () => { await d.scroll(500); await d.scroll(400); },
    },
    {
      say: 'Now a whole payment, end to end, for the example bank. We bind each port to a simulator, choose a salary file, and name the message it is.',
      do: async () => {
        await d.forTenant('example-bank');
        await d.go('tests');
        await d.click(p().getByRole('tab', { name: 'Ad hoc bench' }));
        await d.select(p().getByLabel('Target', { exact: true }), 'p.wf.corporate.v1');
        await d.select(p().getByLabel('Run tenant'), 'example-bank');
        for (const [port, sim] of PLATFORM) {
          await d.click(p().getByRole('button', { name: 'Add a port' }));
          await d.select(p().getByLabel('Port', { exact: true }).last(), port);
          await d.select(p().getByLabel('Simulator', { exact: true }).last(), sim);
          await d.select(p().getByLabel('Scenario', { exact: true }).last(), 'accept');
        }
        await d.click(p().getByRole('tab', { name: 'Inbound sample' }));
        await d.select(p().getByLabel('Inbound sample'), 'samples/wps-sif-valid.sif');
        await d.select(p().getByLabel('Start message'), 'uae.wps.sif');
      },
    },
    {
      say: 'The front door reads the file, picks the corporate workflow, and the WPS leg fills its slot. The payment is screened, funds are reserved, the rail is chosen, the file is submitted and acknowledged, and the payment is posted and settled.',
      do: async () => {
        await d.click(p().getByRole('button', { name: 'Run the bench' }));
        await p().waitForTimeout(2000);
        await main().getByText('Business trace').first().scrollIntoViewIfNeeded();
        await p().waitForTimeout(800);
      },
    },
    {
      say: 'The conformance kit holds forty six scenarios, one for each construct of the language. Every engine must reproduce them: the studio, D T F X, and Flowable.',
      do: async () => { await d.go('kit'); await d.click(p().getByRole('button', { name: /^Run all \d+$/ })); await p().waitForTimeout(2500); },
    },
    {
      say: 'A bank changes values, not logic: which rails it runs, its limits, and the hooks it binds. Each change is a new release of its configuration set, checked against the bounds the scheme allows.',
      do: async () => { await d.go('tenant'); await d.as('example-bank', 'example.maker'); await d.go('tenant'); await d.scroll(450); },
    },
    {
      say: 'An export is the package an engine runs. Each task queue gets its own worker, which serves only its own tasks. The manifest also lists the change sets that every artefact was published in.',
      do: async () => {
        await d.go('export?workflows=p.wf.corporate.v1&target=dtfx');
        await d.select(p().getByLabel('Export tenant'), 'example-bank');
        await d.click(p().getByRole('button', { name: 'Preview' }));
        await p().waitForTimeout(1200);
        await d.scroll(400);
      },
    },
    {
      say: 'Governance keeps the record. Every save, approval and refusal goes into a hash-chained audit log, and the studio can verify the chain at any time.',
      do: async () => {
        await d.go('governance?tab=audit');
        await d.click(p().getByRole('button', { name: 'Verify the chain' }));
        await p().waitForTimeout(800);
      },
    },
    {
      say: 'The AI assistant drafts a rule, an activity, a script or a simulator. Start from a template, fill in the parts in angle brackets, and add the source text. The draft runs through the same gates, and it never publishes itself.',
      do: async () => {
        await d.go('assistant');
        await d.select(p().getByLabel('Draft kind'), 'rule');
        await d.select(p().getByLabel('Prompt template'), 'rule-limit');
        await p().waitForTimeout(800);
      },
    },
    {
      say: 'That is the tour. In the next demo, we build a new scheme and its workflow from scratch, approve it, enable it for a bank, and pay a file end to end.',
      do: async () => { await d.go('catalogue'); },
    },
  ];
}
