import { toYaml } from '../src/core/yaml.js';
import { ACK_IN, FILE_IN, FILE_IN_SCRIPT, FILE_OUT, LEG, PORT, RULE, RULESET, SCHEME, SEND, SIM, SUBFLOW, SUBMIT, VALID, VALIDATE } from '../test/blackbox/demo-scheme.js';
import { SLOW, type Director, type Scene } from './record.js';

// Video 5a, one per console: the studio as a bank's configurator and its approver. The bank workspace; a scheme licensed (the
// configurator proposes, the approver approves); the configuration set and its dials, with bounds and dates; the bank's rules and
// rule tables; its reference tables, the cut-offs among them, and a new table; its port bindings and the rates port; its hooks at
// the platform workflow's hook points; maker-checker and the change set; the bank's configuration on the test bench; the audit
// trail; and what reaches the runtime. Every value is synthetic. With TH_DEMO_DRY=1 the same steps run fast, with no video, and
// each expected result is checked.
//
// Before the camera, the platform's designer publishes the synthetic scheme uae.demo (test/blackbox/demo-scheme.ts) through the
// studio's API, so that the bank has a scheme to license. The designer and developer video (5b) shows how such parts are built.

export const NAME = '5a-bank-configurator';
export const TITLE = 'TransactHub Designer Studio, 5a: the bank configurator and approver';
export const PACE = SLOW;

const BANK = 'example-bank';
const MAKER = 'example.maker';
const APPROVER = 'example.checker';
const PLATFORM: [string, string][] = [
  ['p.port.core-banking.v1', 'p.sim.core-banking.v1'],
  ['p.port.sanctions.v1', 'p.sim.sanctions.v1'],
  ['p.port.fraud.v1', 'p.sim.fraud.v1'],
];
const DEMO: [string, string][] = [[PORT.name, SIM.name], ['uae.p.port.calendar.v1', 'uae.p.sim.calendar.v1'], ['p.port.encryption.v1', 'p.sim.encryption.v1']];
const nextMinor = (r: string) => String(r).replace(/^(\d+)\.(\d+)\..*/, (_: string, M: string, m: string) => `${M}.${Number(m) + 1}.0`);

/** The platform publishes the synthetic scheme uae.demo through the API, as the end-to-end test does, before the camera rolls. */
async function publishDemoScheme(d: Director) {
  const maker = d.api('shared.designer');
  const checker = d.api('shared.checker');
  const must = (what: string, r: { status: number; data: any }) => {
    if (r.status !== 200) throw new Error(`${what}: ${r.status} ${JSON.stringify(r.data).slice(0, 600)}`);
    return r.data;
  };
  must('sample', await maker.post('/api/samples', { name: 'demo-credit-file-valid.csv', content: VALID }));
  const publish = async (docs: any[], script?: [string, string]) => {
    const keys: string[] = [];
    for (const doc of docs) keys.push(must(`draft ${doc.name ?? doc.scope}`, await maker.post('/api/drafts', { doc })).draft.key);
    if (script) must('script', await maker.post('/api/scripts', { activity: script[0], source: script[1] }));
    for (const key of keys) must(`review ${key}`, await maker.post('/api/request-release', { key }));
    must(`publish ${keys[0]}`, await checker.post('/api/publish', { key: keys[0] }));
  };
  await publish([PORT, SIM]);
  await publish([RULE]);
  await publish([RULESET]);
  await publish([VALIDATE]);
  await publish([FILE_IN], [FILE_IN.name, FILE_IN_SCRIPT]);
  await publish([FILE_OUT]);
  await publish([ACK_IN]);
  await publish([SEND]);
  await publish([SCHEME, SUBMIT, SUBFLOW, LEG]);
}

export async function open(d: Director) {
  await publishDemoScheme(d);
  await d.as(BANK, MAKER);
  await d.go('bank');
}

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');
  const panel = (title: RegExp) => main().locator('.panel', { has: p().locator('h3', { hasText: title }) }).first();

  /** Wait until the page shows the text; a step that does not produce it stops the recording. */
  const expectText = async (re: RegExp, seconds = 30, where: any = main()) => {
    for (let i = 0; i < seconds * 4; i++) {
      if (re.test(await where.innerText())) return;
      await p().waitForTimeout(250);
    }
    throw new Error(`The page does not show ${re}`);
  };
  const status = async (re: RegExp, where: any = p()) => {
    for (let i = 0; i < 160; i++) {
      const t = (await where.locator('[role="status"], .error').allTextContents()).join(' ');
      if (re.test(t)) return;
      if (/Refused/.test(t)) throw new Error(t);
      await p().waitForTimeout(250);
    }
    throw new Error(`No status matching ${re}`);
  };
  const licence = () => p().locator('[aria-label="Schemes licensed"]');
  const refTables = () => p().locator('[aria-label="Reference tables"]');
  const bindings = () => p().getByTestId('port-bindings');
  const params = () => panel(/^Parameters/);
  /** Scroll a part of the page to the middle of the screen, above the captions. */
  const centre = async (loc: any) => {
    await loc.evaluate((e: any) => e.scrollIntoView({ block: 'center' }));
    await d.wait(500);
  };
  const filterParams = async (text: string) => {
    await d.type(main().getByRole('searchbox', { name: 'Filter parameters' }), text, 60);
    if (text !== 'set') {
      for (let i = 0; i < 40 && !(await params().locator('tbody tr').first().innerText()).includes(text); i++) await p().waitForTimeout(150);
      if (!(await params().locator('tbody tr').first().innerText()).includes(text)) throw new Error(`The parameters do not show ${text}`);
    }
    await centre(params().locator('tbody tr').first());
  };
  /** Paste a changed configuration set into the editor, from the set the studio returns now. */
  const tenantYaml = async (change: (c: any) => void) => {
    const cs = (await d.api(MAKER).get(`/api/tenants/${BANK}/configuration`)).data;
    const next = JSON.parse(JSON.stringify(cs.configurationSet));
    change(next);
    await d.paste(p().getByLabel('Configuration set YAML'), toYaml(next));
    await d.wait(1200);
  };
  const limit2027 = (c: any) => c.values.find((v: any) => v.parameter === 'b.rule.internal-limit.v1#internalLimit' && String(v.validFrom) === '2027-01-01');
  const nextSet = (limit: number) => (c: any) => {
    c.release = nextMinor(c.release);
    if (!c.accepts.some((a: any) => a.scope === 'uae.demo')) c.accepts.push({ scope: 'uae.demo', release: '1.0.0' });
    const rails = c.values.find((v: any) => v.parameter === 'p.table.rail-choice.v1#enabledRails');
    if (!rails.value.includes('uae.demo')) rails.value.push('uae.demo');
    limit2027(c).value = limit;
  };
  const draftRow = (match: RegExp | string) => main().locator('tr', { hasText: match }).first();
  const approve = async (match: RegExp | string) => {
    await d.click(draftRow(match).getByRole('button', { name: 'Approve as version' }));
    await status(/approved as a version/);
    await d.wait(800);
  };
  const benchTab = async () => {
    await d.click(p().getByRole('tab', { name: 'Ad hoc bench' }));
  };
  const run = async (expect: RegExp) => {
    await d.click(p().getByRole('button', { name: 'Run the bench' }));
    await expectText(expect, 60);
    await main().getByText('Business trace').first().scrollIntoViewIfNeeded();
    await d.wait(900);
  };

  return [
    // ------------------------------------------------------------------ opening
    {
      card: ['5a. The bank configurator', 'The studio as a bank\'s configurator and its approver, every feature'],
      say: 'This video shows the studio as a bank uses it. A bank does not write the shared payment workflows. It configures them: its values, its own rules and tables, its connections, and its own small steps at the places the platform leaves open. Every change is made by one person and approved by another. Every value you will see is made up for training.',
    },
    {
      say: 'Two people work for the example bank. The bank configurator makes changes. The bank approver, a different person, approves them. We sign in first as the configurator. The yellow badge in the top bar says we work for a bank tenant, and the strip says so again: we work in scope b, the bank\'s own scope, and shared content is read only.',
      do: async () => {
        await d.point(p().locator('header .topbar-badge').first(), 2000);
        await d.point(main().locator('.tenant-strip'), 2200);
        await d.point(p().locator('nav').first(), 2200);
      },
    },

    // ------------------------------------------------------------------ 1. the bank workspace
    {
      card: ['1. The bank workspace', 'Everything that is the bank\'s own, on one page'],
      say: 'The bank workspace is where a configurator lands. On the left, the bank\'s own parts: its hooks and the activities they call, its rules, its simulators, its port bindings, its calendar and cut-off tables, and the schemes it licenses. On the right, a summary of its dials, its hook bindings, and a test bench for every workflow it can configure.',
      do: async () => {
        await d.point(main().locator('.view-head').first(), 2500);
        await d.point(panel(/^Hooks/), 2500);
        await d.point(panel(/^Dials/), 2000);
      },
    },
    {
      say: 'The bank has three hooks. One adds a cost centre before screening, one holds a large payment for approval before it is released, and one tells the bank\'s treasury when a payment closes. Each is bound at a hook point of the platform\'s workflow. We come back to them later.',
      do: async () => {
        await d.point(panel(/^Hooks/).locator('ul').first(), 2500);
        await d.point(panel(/^Hook bindings/), 3000);
      },
    },
    {
      say: 'Its rules and rule values: one rule of its own, an internal limit. Its simulators stand in for the bank\'s own systems on the test bench: the treasury, and the reference data service.',
      do: async () => {
        await d.point(panel(/^Rules and rule values/), 2500);
        await d.point(panel(/^Simulators and scenarios/), 2500);
      },
    },

    // ------------------------------------------------------------------ 2. licensing a scheme
    {
      card: ['2. Licensing a scheme', 'The configurator proposes, the approver approves'],
      say: 'A bank runs only the schemes it licenses. Today the example bank licenses one scheme, W P S, the salary scheme. The platform has just published a second scheme, U A E demo, a synthetic one made up for training. It is not licensed yet.',
      do: async () => {
        await licence().scrollIntoViewIfNeeded();
        await d.point(licence().locator('p').first(), 2500);
        await d.point(licence().locator('tr', { hasText: 'uae.demo' }), 2500);
      },
    },
    {
      say: 'The configurator proposes the licence. The studio writes the bank\'s next lock file, the list of what the bank runs, with U A E demo added, and saves it as a draft. It is not in force yet.',
      do: async () => {
        await d.click(licence().getByRole('button', { name: 'License uae.demo' }));
        await status(/Proposed lock file/, licence());
        await d.point(licence().getByRole('status'), 2500);
        await d.point(licence().getByTestId('licence-draft'), 2000);
      },
    },
    {
      say: 'Now we sign in as the bank approver, another person. Here the same row says proposed, and the approver has a button the configurator does not have: approve the licence.',
      do: async () => {
        await d.as(BANK, APPROVER);
        await d.go('bank');
        await licence().scrollIntoViewIfNeeded();
        await d.point(licence().getByTestId('licence-draft'), 2500);
      },
    },
    {
      say: 'Approved. The approval publishes the lock file as one change set, and builds the bank\'s next distribution, the signed package its runtime installs, which now carries U A E demo. A configuration that uses a scheme the bank does not license is refused at approval.',
      do: async () => {
        await d.click(licence().getByRole('button', { name: 'Approve the licence' }));
        await status(/approved in change set/, licence());
        await d.point(licence().getByRole('status'), 3500);
        await d.point(licence().locator('tr', { hasText: 'uae.demo' }), 2000);
      },
    },

    // ------------------------------------------------------------------ 3. the configuration set and its dials
    {
      card: ['3. The configuration set', 'Values, called dials, with bounds and dates'],
      say: 'Back as the configurator, on the tenant page. The bank\'s values live in one document, its configuration set, with a release number. It names the shared releases the bank accepts, and who made and who approved it.',
      do: async () => {
        await d.as(BANK, MAKER);
        await d.go('tenant');
        await d.point(panel(/^Configuration set/), 3500);
      },
    },
    {
      say: 'Every value a bank may change is a parameter, a dial, declared by the shared content with a default and bounds. The workflows never hold a constant a bank might want to change. Here is the bank\'s internal limit: a default of one million, and a bound of at most one million.',
      do: async () => {
        await filterParams('internalLimit');
        await d.point(params().locator('tbody tr').first(), 3000);
      },
    },
    {
      say: 'And here are the bank\'s values for it, each with dates. Two hundred and fifty thousand until the end of twenty twenty six, then one hundred thousand from January. Values are looked up by date, so a payment in flight sees the value of its own day.',
      do: async () => {
        await d.point(params().locator('tbody tr').first().locator('td').last(), 3500);
      },
    },
    {
      say: 'The filter word set shows only the dials this bank has set. The rails it enables, the routing codes that are its own, its limits, and more.',
      do: async () => {
        await p().getByRole('searchbox', { name: 'Filter parameters' }).fill('');
        await filterParams('set');
        await d.point(params(), 3000);
        await d.scroll(400);
      },
    },
    {
      say: 'Below them, the hook points: the places in the platform\'s workflows where a bank may plug in a step of its own. Each says what the hook may read and write, its type, and which of the bank\'s hooks is bound there, from which date.',
      do: async () => {
        await panel(/^Hook points/).scrollIntoViewIfNeeded();
        await d.point(panel(/^Hook points/), 4000);
      },
    },
    {
      say: 'Now a change. The bank accepts U A E demo, enables its rail, and changes next year\'s limit. We edit the set as text, and every edit is checked. First a mistake: a limit of two million, above the bound of one million.',
      do: async () => {
        await p().getByLabel('Configuration set YAML').scrollIntoViewIfNeeded();
        await tenantYaml(nextSet(2_000_000));
        await expectText(/L14/);
        await d.point(main().getByText('L14').first(), 3000);
      },
    },
    {
      say: 'The check says L fourteen: a value outside its bounds. The save button stays off. We correct it to one hundred and fifty thousand. Now the set passes every check, and the release number has moved on by one.',
      do: async () => {
        await tenantYaml(nextSet(150_000));
        await expectText(/passes every check/);
        await d.point(main().getByText('passes every check').first(), 2500);
      },
    },
    {
      say: 'We save it. The save needs no approval, because it is only a draft. It is in force only when the bank approver makes it a version.',
      do: async () => {
        await d.click(p().getByRole('button', { name: 'Save the configuration set' }));
        await status(/Saved .* as a draft/);
        await d.point(main().getByRole('status').first(), 2500);
      },
    },
    {
      say: 'A second way in: parameters this bank may set, with set a value beside each one. It adds the dial to the set with its default, ready to change. A bank may set only what the shared content declares settable.',
      do: async () => {
        await panel(/^Parameters this bank may set/).scrollIntoViewIfNeeded();
        await d.point(panel(/^Parameters this bank may set/), 3500);
      },
    },

    // ------------------------------------------------------------------ 4. rules and rule tables
    {
      card: ['4. The bank\'s rules and rule tables', 'A rule with a parameter, and a decision table it sets'],
      say: 'The bank\'s own rule, the internal limit, opens in the rule editor. Its name starts with b, the bank\'s scope. It declares its parameter, internal limit, with the default and the bounds we just met, so the bank sets the number without touching the rule.',
      do: async () => {
        await d.go('editors?kind=rule&open=b.rule.internal-limit.v1');
        await expectText(/internalLimit/);
        await main().getByText('Parameters').first().scrollIntoViewIfNeeded();
        await d.wait(1200);
        await d.point(panel(/^Parameters/), 3000);
      },
    },
    {
      say: 'Its worked examples run on every edit: a payment under the limit passes, and one over it gives the finding A M zero two. The gates on the right are green. A change to the rule would be a new release, made and approved like any other.',
      do: async () => {
        await main().getByText('Examples').first().scrollIntoViewIfNeeded();
        await d.wait(1500);
        await d.point(panel(/^Examples/), 2500);
        await d.point(panel(/^Gates/), 2000);
      },
    },
    {
      say: 'Some choices are decision tables, rows of when and then. The rail choice table decides which rail a payment takes. It is shared content, so the bank cannot edit it, but its enabled rails are a dial, and the bank has just added U A E demo to them.',
      do: async () => {
        await d.go('artefact/p.table.rail-choice.v1');
        await d.wait(1200);
        await d.scroll(400);
        await d.wait(1500);
      },
    },

    // ------------------------------------------------------------------ 5. reference tables
    {
      card: ['5. Reference tables', 'The calendar, the cut-offs, the branches, and a new table'],
      say: 'Reference tables hold the bank\'s own lists, each in editions by date. The bank\'s calendar of closing days, its cut-off times, its branches, and the exchange rates from its rates service. A new edition starts on its valid from date, and earlier editions stay as they were.',
      do: async () => {
        await d.go('bank');
        await refTables().scrollIntoViewIfNeeded();
        await d.point(refTables(), 3000);
      },
    },
    {
      say: 'The cut-off table. For each scheme and direction, the time of day after which the bank takes a file for the next business day, or refuses it. Today, an outward W P S file after two in the afternoon, Dubai time, waits for the next business day.',
      do: async () => {
        await d.select(refTables().getByLabel('Reference table'), 'b.ref.cut-offs.v1');
        await d.wait(800);
        await d.point(refTables().locator('table').first(), 3500);
      },
    },
    {
      say: 'From the first of January, the bank moves that cut-off to half past one. We give the new edition its date, change the time, and check it. Every row fits its columns, and the editions run in date order.',
      do: async () => {
        await d.paste(refTables().getByLabel('Valid from'), '2027-01-01');
        await d.type(refTables().getByLabel('cutOff 1', { exact: true }), '13:30', 120);
        await d.click(refTables().getByRole('button', { name: 'Check' }));
        await d.wait(800);
        await d.point(refTables().locator('.issues, .pass-text, p').last(), 2000);
      },
    },
    {
      say: 'Saved as a draft, at the table\'s next minor release. It reaches the runtime only once the approver approves it, in the bank\'s configuration release, never in a distribution.',
      do: async () => {
        await d.click(refTables().getByRole('button', { name: 'Save as draft' }));
        await status(/Saved as draft/, refTables());
        await d.point(refTables().getByRole('status'), 2500);
      },
    },
    {
      say: 'A bank can also start a table of its own. New table opens a form: a name in the bank\'s scope, a title, a domain, and its columns. Ours lists the bank\'s payroll service desks: a code, which is the key, a title, and the branch that hosts it.',
      do: async () => {
        await d.click(refTables().getByRole('button', { name: 'New table' }));
        const form = p().locator('[aria-label="New reference table"]');
        await d.type(form.getByLabel('Table name'), 'payroll-desks', 70);
        await d.type(form.getByLabel('Table title'), 'Payroll service desks', 50);
        await d.type(form.getByLabel('Table domain'), 'payroll-desks', 50);
        await d.type(form.getByLabel('Column title 1'), 'Desk code', 50);
      },
    },
    {
      say: 'Add a column for the title, of type text, required. And one for the branch, a code. Start the table: the studio checks the columns and the key first, so the rows we write next will fit them.',
      do: async () => {
        const form = p().locator('[aria-label="New reference table"]');
        await d.click(form.getByRole('button', { name: 'Add a column' }));
        await d.type(form.getByLabel('Column id 2'), 'title', 60);
        await d.type(form.getByLabel('Column title 2'), 'Title', 60);
        await d.check(form.getByLabel('Column required 2'));
        await d.click(form.getByRole('button', { name: 'Add a column' }));
        await d.type(form.getByLabel('Column id 3'), 'branch', 60);
        await d.select(form.getByLabel('Column type 3'), 'code');
        await d.type(form.getByLabel('Column title 3'), 'Branch', 60);
        await d.click(form.getByRole('button', { name: 'Start the table' }));
        await expectText(/payroll-desks\.v1, new/, 20, refTables());
      },
    },
    {
      say: 'Its first edition: valid from the first of December, with two desks. Every value is made up. Check, and save as a draft. Its first release is one point zero point zero.',
      do: async () => {
        await d.paste(refTables().getByLabel('Valid from'), '2026-12-01');
        await d.click(refTables().getByRole('button', { name: 'Add a row' }));
        await d.type(refTables().getByLabel('code 1', { exact: true }), 'PD-01', 60);
        await d.type(refTables().getByLabel('title 1', { exact: true }), 'Payroll desk, head office', 40);
        await d.type(refTables().getByLabel('branch 1', { exact: true }), 'BR-001', 60);
        await d.click(refTables().getByRole('button', { name: 'Add a row' }));
        await d.type(refTables().getByLabel('code 2', { exact: true }), 'PD-02', 60);
        await d.type(refTables().getByLabel('title 2', { exact: true }), 'Payroll desk, north', 40);
        await d.type(refTables().getByLabel('branch 2', { exact: true }), 'BR-002', 60);
        await d.click(refTables().getByRole('button', { name: 'Check' }));
        await d.click(refTables().getByRole('button', { name: 'Save as draft' }));
        await status(/Saved as draft .*b\.ref\.payroll-desks\.v1/, refTables());
        await d.point(refTables().getByRole('status'), 2000);
      },
    },

    // ------------------------------------------------------------------ 6. port bindings and the rate feed
    {
      card: ['6. Port bindings and the rate feed', 'Which adapter answers each port, from which date'],
      say: 'A port is a door to another system: core banking, sanctions screening, a scheme. The platform declares the doors. The bank says which adapter answers each one in its runtime, from which date, with which settings. Here, core banking is answered by a simulator until December, then by the bank\'s gateway.',
      do: async () => {
        await bindings().scrollIntoViewIfNeeded();
        await d.point(bindings().locator('table').first(), 4000);
      },
    },
    {
      say: 'Now the rates port, for exchange rates. We add a binding: the rates port, answered by the bank\'s rates service through its REST gateway, from the first of December, refreshed every sixty minutes.',
      do: async () => {
        await d.click(bindings().getByRole('button', { name: 'Add binding' }));
        const n = await bindings().locator('tbody tr').count();
        await d.select(bindings().getByLabel(`Port ${n}`, { exact: true }), 'p.port.fx-rates.v1');
        await d.type(bindings().getByLabel(`Adapter ${n}`, { exact: true }), 'gateway-rest', 60);
        await d.paste(bindings().getByLabel(`Valid from ${n}`, { exact: true }), '2026-12-01');
        await bindings().locator('.table-wrap').first().evaluate((e: any) => { e.scrollLeft = e.scrollWidth; });
        await d.type(bindings().getByLabel(`Settings ${n}`, { exact: true }), 'baseUrl=https://rates.example.invalid\nrefreshMinutes=60', 40);
      },
    },
    {
      say: 'A credential is never written here. The binding names a secret reference, and the runtime reads the secret from its own store. Check: every binding names a port the bank may bind, an adapter and a date. We save it as a draft.',
      do: async () => {
        const n = await bindings().locator('tbody tr').count();
        await d.type(bindings().getByLabel(`Secret reference ${n}`, { exact: true }), 'env:FX_RATES_TOKEN', 60);
        await d.click(bindings().getByRole('button', { name: 'Check' }));
        await expectText(/credentials are references/, 20, bindings());
        await d.click(bindings().getByRole('button', { name: 'Save draft' }));
        await expectText(/Saved as draft/, 20, bindings());
        await d.point(bindings().getByText(/Saved as draft/), 2500);
      },
    },
    {
      say: 'On the runtime, the rate feed asks that binding for new rates on that schedule. Each time, it publishes them as a signed configuration release of the bank: an edition of the exchange rates table, and the rates value the payments read.',
      do: async () => {
        await refTables().scrollIntoViewIfNeeded();
        await d.select(refTables().getByLabel('Reference table'), 'b.ref.fx-rates.v1');
        await d.wait(800);
        await d.point(refTables().locator('table').first(), 3500);
      },
    },
    {
      say: 'A payment refuses a rate older than the bank allows. The activity that reads the rates declares that limit as a dial too, max rate age: twenty four hours by default, from one hour to seven days.',
      do: async () => {
        await d.go('artefact/p.enrich.fx-rate.v1');
        await expectText(/maxRateAge/);
        await centre(main().getByText('maxRateAge').first());
        await d.point(main().getByText('maxRateAge').first().locator('xpath=ancestor::tr[1]'), 3500);
      },
    },
    {
      say: 'Who approves an hourly rate release? Not a person each hour. In the service console, a bank approver grants a standing consent, once, for rate releases only, and can revoke it. Any other release still waits for its own consent. We do not show the service console here: it has a video of its own.',
      do: async () => { await d.wait(1500); },
    },

    // ------------------------------------------------------------------ 7. hooks
    {
      card: ['7. Hooks', 'The bank\'s own steps, at the platform workflow\'s hook points'],
      say: 'A hook is a small workflow of the bank\'s own. It plugs into a hook point and cannot change the shared workflow around it. The corporate payment workflow leaves a hook point before release. The bank binds its pre release approval hook there.',
      do: async () => {
        await d.go('designer?open=b.hook.pre-release-approval.v1');
        await expectText(/pre-release-approval/);
        await d.wait(1500);
        await d.point(main().locator('.cv-steps').first(), 3500);
      },
    },
    {
      say: 'The hook calls the bank\'s internal limit check. If the check finds a payment over the limit, the hook answers hold, and the payment waits for a person. Otherwise it answers continue. The checks under the canvas say every outcome is handled.',
      do: async () => {
        await d.point(p().getByLabel('Workflow YAML'), 3000);
        await main().getByText('Checks').first().scrollIntoViewIfNeeded();
        await d.wait(1500);
      },
    },
    {
      say: 'Before the bank accepts a new shared release, it runs the compatibility run. Each bound hook point must still exist with a contract that fits, and each of the bank\'s hook tests must pass. Today the answer is may accept.',
      do: async () => {
        await d.go('tenant');
        await panel(/^Compatibility run/).scrollIntoViewIfNeeded();
        await d.click(panel(/^Compatibility run/).getByRole('button', { name: 'Run' }));
        await expectText(/may accept|do not accept/, 60);
        await d.point(panel(/^Compatibility run/), 4000);
      },
    },

    // ------------------------------------------------------------------ 8. maker-checker and the change set
    {
      card: ['8. Maker and checker', 'The change set, approved by another person'],
      say: 'In Governance, the configurator sees the drafts of the bank: the configuration set, the cut-off edition, the new table and the port bindings. Each shows its errors, none, and the drafts it publishes with. The studio builds the change set itself: the draft, and every draft that refers to it or that it refers to.',
      do: async () => {
        await d.go('governance');
        await expectText(/configuration-set|config:example-bank/);
        await d.point(main().locator('table').first(), 4500);
      },
    },
    {
      say: 'The configurator requests the release of each one. The configurator has no approve button: a maker can never approve their own change.',
      do: async () => {
        for (let i = 0; i < 6; i++) {
          const b = main().locator('button:not([disabled])', { hasText: 'Request release' });
          if (!(await b.count())) break;
          await d.click(b.first());
          await status(/Release requested/);
        }
        await d.point(main().locator('table').first(), 2500);
      },
    },
    {
      say: 'The bank approver signs in. The same list, now with approve as version. First the new table, a change set of its own. Approval checks it with everything else in place and runs the tests, and the studio builds the bank\'s signed configuration release that carries it.',
      do: async () => {
        await d.as(BANK, APPROVER);
        await d.go('governance');
        await approve(/b\.ref\.payroll-desks\.v1/);
        await d.point(main().getByRole('status').first(), 3500);
      },
    },
    {
      say: 'Next the configuration set. Its change set holds the cut-off edition too, so one approval publishes both, and both are in the next configuration release. Then the port bindings. Had any of them an error or a red test, the approval would refuse, and nothing of its change set would be published.',
      do: async () => {
        await approve(/config:example-bank/);
        await d.point(main().getByRole('status').first(), 3000);
        if (await draftRow(/port-bindings|Port bindings/).count()) await approve(/port-bindings|Port bindings/);
        await expectText(/No drafts\./);
        await d.point(main().getByRole('status').first(), 2500);
      },
    },

    // ------------------------------------------------------------------ 9. the bench
    {
      card: ['9. Testing the bank\'s configuration', 'The test bench, with the bank\'s own values'],
      say: 'Now we test the bank\'s configuration on the test bench. The bench runs a whole payment on the studio\'s own clock, with simulators standing in for every other system. No real payment is touched. We run the corporate workflow for the example bank, from its channel, with a demo credit file.',
      do: async () => {
        await d.as(BANK, MAKER);
        await d.go('bank');
        await d.click(panel(/^Test bench by workflow/).getByRole('link', { name: 'p.wf.corporate.v1' }));
        await benchTab();
        await d.select(p().getByLabel('Target', { exact: true }), 'p.wf.corporate.v1');
        for (const [port, sim] of [...PLATFORM, ...DEMO]) {
          await d.click(p().getByRole('button', { name: 'Add a port' }));
          await d.select(p().getByLabel('Port', { exact: true }).last(), port);
          await d.select(p().getByLabel('Simulator', { exact: true }).last(), sim);
          await d.select(p().getByLabel('Scenario', { exact: true }).last(), 'accept');
        }
        await d.click(p().getByRole('tab', { name: 'Inbound sample' }));
        await d.select(p().getByLabel('Inbound sample'), 'samples/demo-credit-file-valid.csv');
        await d.select(p().getByLabel('Start message'), 'uae.demo.credit-file');
      },
    },
    {
      say: 'Run. The dispatch table of U A E demo starts the corporate workflow. The bank\'s hooks run at their hook points, the route finds the rail the bank has just enabled, the scheme\'s leg runs in its slot, and the payment settles. Without the licence and the new configuration, the route would have found no rail.',
      do: async () => {
        await run(/route:decided[\s\S]*end:settled|end:settled/);
        await d.point(main().getByText('Business trace').first().locator('..'), 4000);
      },
    },

    // ------------------------------------------------------------------ 10. the audit trail
    {
      card: ['10. The audit trail', 'Every save, request, approval and refusal'],
      say: 'Governance keeps the record. The audit log lists every save, request, approval and refusal, with who did it and when. The configurator\'s saves are here, and the approver\'s approvals.',
      do: async () => {
        await d.as(BANK, APPROVER);
        await d.go('governance?tab=audit');
        await d.wait(1200);
        await d.point(main().locator('table').first(), 3500);
      },
    },
    {
      say: 'Each entry carries the hash of the one before it, so the log is a chain. Verify the chain recomputes it. If anybody had changed an entry, the chain would break at that entry.',
      do: async () => {
        await d.click(p().getByRole('button', { name: 'Verify the chain' }));
        await expectText(/The chain verifies/);
        await d.point(main().getByText('The chain verifies').first(), 3000);
      },
    },

    // ------------------------------------------------------------------ 11. what reaches the runtime
    {
      card: ['11. What reaches the runtime', 'A signed configuration release, put in force with consent'],
      say: 'What reaches the bank\'s runtime? Two kinds of package. A distribution carries the logic: the shared packs the bank accepts, by hash and unchanged, the schemes it licenses, and its bank pack. A configuration release carries the values: the configuration set, the reference tables and the port bindings.',
      do: async () => {
        await d.as(BANK, MAKER);
        await d.go('tenant');
        await panel(/^Tenant export/).scrollIntoViewIfNeeded();
        await d.point(panel(/^Tenant export/), 4000);
      },
    },
    {
      say: 'Both are signed by the studio when the approver approves. On the runtime, a bank approver puts each in force in the service console, with a consent of its own, and the rate feed\'s releases on the standing consent. The service console has its own video. The studio never reads or changes a running payment.',
      do: async () => { await d.point(panel(/^Configuration set/), 3000); },
    },
    {
      card: ['The bank configures. The approver approves.', 'Next: the studio as designer and developer'],
      say: 'That is the studio for a bank. A workspace of its own. A scheme licensed by two people. Values with bounds and dates. Its rules, its tables, its port bindings and its hooks. Every change made by one person and approved by another, tested on the bench, and kept in the audit log. The next video shows the studio as the platform\'s designer and developer.',
      do: async () => { await d.wait(1500); await d.go('bank'); },
    },
  ];
}
