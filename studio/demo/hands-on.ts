import { toYaml } from '../src/core/yaml.js';
import * as D from './hands-on/docs.js';
import { SLOW, type Director, type Scene } from './record.js';

// Training video 2: hands-on. One long recording, at the slow pace, in six parts:
//   1. a rule, built in the form and tested by its examples;
//   2. an activity that runs the rule, tested on the bench;
//   3. the AI assistant, from a prompt template;
//   4. a leg composed on the canvas from blocks that already exist;
//   5. what to build when nothing exists (the scheme from scratch, in outline);
//   6. a bank's own rule and hook, its configuration, and a payment held for approval.
// Every value is synthetic. With TH_DEMO_DRY=1 the same steps run fast with no video, and each
// expected result is checked, so the dry run is also a test of the steps.

export const NAME = 'training-2-hands-on';
export const TITLE = 'TransactHub Designer Studio, training 2: hands-on';
export const PACE = SLOW;

const PLATFORM: [string, string, string?][] = [
  ['p.port.core-banking.v1', 'p.sim.core-banking.v1'],
  ['p.port.sanctions.v1', 'p.sim.sanctions.v1'],
  ['p.port.fraud.v1', 'p.sim.fraud.v1'],
];
const AI_LIVE = !!(process.env.ANTHROPIC_API_KEY || process.env.ANTHROPIC_AUTH_TOKEN);

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');

  // ---------------------------------------------------------------- helpers

  const status = async (re: RegExp) => {
    for (let i = 0; i < 120; i++) {
      if ((await p().locator('[role="status"]').allTextContents()).some((t: string) => re.test(t))) return;
      await p().waitForTimeout(250);
    }
    throw new Error(`No status matching ${re}`);
  };
  /** Wait until the page shows the text; a step that does not produce it stops the recording. */
  const expectText = async (re: RegExp) => {
    for (let i = 0; i < 80; i++) {
      if (re.test(await main().innerText())) return;
      await p().waitForTimeout(250);
    }
    throw new Error(`The page does not show ${re}`);
  };
  const expectCount = async (loc: any, n: number) => {
    for (let i = 0; i < 80; i++) {
      if ((await loc.count()) === n) return;
      await p().waitForTimeout(250);
    }
    throw new Error(`Expected ${n} of ${loc}, found ${await loc.count()}`);
  };
  const startNew = async (kind: string) => {
    await d.go(`editors?kind=${kind}`);
    await d.click(p().getByRole('button', { name: 'Start new' }));
  };
  const pasteDoc = async (kind: string, doc: any) => {
    await startNew(kind);
    await d.click(p().getByRole('button', { name: 'Edit the YAML' }).first());
    await d.paste(p().getByLabel('Document YAML').first(), toYaml(doc));
    await d.click(p().getByRole('button', { name: 'Done' }).first());
  };
  const save = async () => {
    await d.click(p().getByRole('button', { name: /^Save draft as / }).first());
    await status(/Saved draft/);
  };
  const publish = async (checker = 'shared.checker') => {
    await d.select(p().getByLabel('Checker').first(), checker);
    await d.wait(600);
    await d.click(p().getByRole('button', { name: `Publish as ${checker}` }).first());
    await status(/Change set CS-\S+ published/);
    await d.wait(700);
  };
  const addChip = async (label: string, value: string) => {
    await d.type(p().getByLabel(label, { exact: true }).first(), value, 40);
    await p().getByLabel(label, { exact: true }).first().press('Enter');
    await d.wait(500);
  };
  const removeChip = async (value: string) => {
    await d.click(main().getByRole('button', { name: `Remove ${value}`, exact: true }).first());
  };
  const bench = async (target: string, tenant: string, ports: [string, string, string?][]) => {
    await d.go('tests');
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
  const withContext = async (json: string) => {
    await d.click(p().getByRole('button', { name: 'Context as JSON' }));
    await d.paste(p().getByLabel('Context JSON'), json);
  };
  const withSample = async (sample: string, message?: string) => {
    await d.click(p().getByRole('button', { name: 'Inbound sample' }));
    await d.select(p().getByLabel('Inbound sample'), sample);
    if (message) await d.select(p().getByLabel('Start message'), message);
  };
  const run = async (expect: RegExp) => {
    await d.click(p().getByRole('button', { name: 'Run the bench' }));
    await expectText(expect);
    await main().getByText('Business trace').first().scrollIntoViewIfNeeded();
    await d.wait(900);
  };
  const designer = async (kind: 'workflow' | 'hook', wfKind: string | undefined, doc: any) => {
    await d.go('designer');
    await d.select(p().getByLabel('Start from'), { label: 'Blank' });
    await d.select(p().getByLabel('Kind', { exact: true }), kind);
    if (wfKind) await d.select(p().getByLabel('Workflow kind'), wfKind);
    await d.type(p().getByLabel('Name', { exact: true }), doc.name, 25);
    await d.type(p().getByLabel('Title', { exact: true }), doc.title, 15);
    await d.click(p().getByRole('button', { name: 'Create' }));
    // The blank starter loads from the server; paste only once it is in, or it overwrites the paste.
    for (let i = 0; i < 80 && !(await p().getByLabel('Workflow YAML').inputValue()).includes(doc.name); i++) await p().waitForTimeout(100);
    await d.paste(p().getByLabel('Workflow YAML'), toYaml(doc));
    await d.wait(900);
  };
  /** On the canvas: insert a call step at a top-level position, pick its block, name it, handle its outcomes. */
  const canvasCall = async (at: number, id: string, search: string, block: string, ends: number) => {
    await d.click(main().locator('.cv-steps').first().locator(':scope > li > .cv-insert > button.cv-plus').nth(at));
    await d.click(main().locator('.cv-palette').getByRole('button', { name: 'call', exact: true }));
    await d.type(p().getByLabel('Search the catalogue'), search, 60);
    await d.click(main().locator('.picker-item').filter({ hasText: block }).first());
    await d.paste(p().getByLabel('Step id'), id);
    for (let i = 0; i < ends; i++) await d.select(main().locator('.cv-edit').getByLabel('Action').nth(i), 'end');
    await d.wait(600);
  };
  const tenantYaml = async (change: (c: any) => void) => {
    const cs = await (await fetch(`${d.server.url}/api/tenants/example-bank/configuration`)).json();
    const next = JSON.parse(JSON.stringify(cs.configurationSet));
    change(next);
    await d.paste(p().getByLabel('Configuration set YAML'), toYaml(next));
    await d.wait(1200);
  };
  const bankConfig = (maxLines: number) => (c: any) => {
    c.release = '1.1.0';
    c.values.push({ parameter: `${D.BANK_RULE.name}#maxLines`, value: maxLines, validFrom: '2026-01-01' });
    c.bindings.push({ hookPoint: 'p.seg.check-and-screen.v1#after-validate', hook: D.BANK_HOOK.name, release: '1.0.0', validFrom: '2026-01-01' });
  };

  // ---------------------------------------------------------------- the scenes

  return [
    {
      card: ['Training 2: hands-on', 'Six parts. Pause at any time and follow along in your own studio.'],
      say: 'Welcome to the second training video. In the first one, we met the ideas: stations, quality checks, recipes, doors and the franchise. Now we use them, slowly, one click at a time. Every value you will see is made up for training.',
      do: async () => { await d.go('catalogue'); },
    },
    {
      say: 'We will build six things. A rule. An activity that runs the rule. A rule drafted with the AI assistant. A workflow composed from parts that already exist. What to do when nothing exists. And last, a bank\'s own rule and hook.',
      do: async () => { await d.point(main().locator('.view-head').first(), 2500); },
    },

    // ------------------------------------------------------------ part 1: a rule
    {
      card: ['Part 1: a rule', 'One quality check, with its own examples'],
      say: 'Part one: a rule. A rule is one quality check that answers yes or no. Like a kitchen check that says: the date on this label must be a real date. Our rule checks the salary month on a salary file. It is written as month and year, six digits, and month thirteen does not exist.',
      do: async () => { await startNew('rule'); },
    },
    {
      say: 'The editor starts from a sample rule, and we change it. First the name. A name has a scope, here U A E W P S, the kind of thing, rule, a short name, and a version. Then a title in plain words.',
      do: async () => {
        await d.type(p().getByLabel('Name', { exact: true }), D.MONTH_RULE.name, 45);
        await d.type(p().getByLabel('Title', { exact: true }), D.MONTH_RULE.title, 45);
      },
    },
    {
      say: 'The rule kind says what sort of check it is. Field means it looks at one value on its own. The level says where the value lives: on the whole file, which is a batch, rather than on each line.',
      do: async () => {
        await d.select(p().getByLabel('Rule kind'), 'field');
        await d.select(p().getByLabel('Rule level'), 'batch');
      },
    },
    {
      say: 'Now the When part: what the rule looks for. A pattern finds a fact. Here the fact is the salary file\'s own details, called W P S batch, and we call it w for short. The sample rule had a second condition. We do not need it, so we remove it.',
      do: async () => {
        await d.type(main().locator('.cond-row').first().getByLabel('Fact'), 'WpsBatch', 70);
        await d.type(main().locator('.cond-row').first().getByLabel('Binds as'), 'w', 70);
        await d.click(main().locator('.cond-row').nth(1).getByRole('button', { name: 'Remove' }));
      },
    },
    {
      say: 'The where part is the question itself. Read it like this: the salary month does not match the shape of a real month. Zero one to twelve, then a four digit year. When that is true, the rule has found a problem.',
      do: async () => { await d.type(main().locator('.cond-row').first().getByLabel('Where'), D.MONTH_RULE.where, 35); },
    },
    {
      say: 'The Then part says what happens when the problem is found. We reject the file. The path points at the value that is wrong. And the reason code is a short standard code that every bank understands. D T zero one means an invalid date.',
      do: async () => {
        await d.select(p().getByLabel('Severity', { exact: true }), 'reject');
        await d.type(p().getByLabel('Finding path', { exact: true }), D.MONTH_RULE.path, 45);
        await d.type(p().getByLabel('Reason code', { exact: true }), D.MONTH_RULE.code, 90);
      },
    },
    {
      say: 'Now the most important part: the examples. An example is a small test. It gives the rule some made up facts and says what should happen. The first example, passes, has October twenty twenty six. We expect no problem.',
      do: async () => {
        await main().getByText('Examples').first().scrollIntoViewIfNeeded();
        await d.paste(p().getByLabel('Given of passes'), JSON.stringify({ WpsBatch: { salaryMonth: '102026' } }, null, 1));
      },
    },
    {
      say: 'The second example, fails, has month thirteen. We expect the rule to reject it with D T zero one. So we remove the sample code and add ours.',
      do: async () => {
        await d.paste(p().getByLabel('Given of fails'), JSON.stringify({ WpsBatch: { salaryMonth: '132026' } }, null, 1));
        await removeChip('NARR');
        await addChip('Expected reason codes of fails', D.MONTH_RULE.code);
      },
    },
    {
      say: 'A good habit: test the edge. December is month twelve, the last real month, so it must pass. We add a third example, give it December, and name it.',
      do: async () => {
        await d.click(p().getByRole('button', { name: 'Add an example' }));
        await d.paste(p().getByLabel('Given of example 3'), JSON.stringify({ WpsBatch: { salaryMonth: '122026' } }, null, 1));
        await d.type(main().getByLabel('Example name').nth(2), 'boundary, December', 45);
      },
    },
    {
      say: 'Look at the gates on the right. They run on every change: the shape of the document, the static checks, and every example. All green. Each example shows pass.',
      do: async () => { await expectText(/boundary, December/); await d.point(main().locator('.col.side').first(), 3500); },
    },
    {
      say: 'Let us break it on purpose, to see what a failure looks like. We change the passing example to month zero zero. The rule now rejects it, but we said it should pass, so the example turns red and shows what actually happened.',
      do: async () => {
        await d.paste(p().getByLabel('Given of passes'), JSON.stringify({ WpsBatch: { salaryMonth: '002026' } }, null, 1));
        await expectCount(main().locator('.example.ex-fail'), 1);
        await d.wait(1500);
        await d.point(main().locator('.example').first(), 3000);
      },
    },
    {
      say: 'Month zero zero really is wrong, so here the example was wrong, not the rule. We put October back, and everything is green again. That is how you test a rule: examples that pass, examples that fail, and the edge.',
      do: async () => {
        await d.paste(p().getByLabel('Given of passes'), JSON.stringify({ WpsBatch: { salaryMonth: '102026' } }, null, 1));
        await expectCount(main().locator('.example.ex-pass'), 3);
        await d.wait(1500);
        await d.point(main().locator('.example').first(), 2000);
      },
    },
    {
      say: 'We save it as a draft. A draft is ours alone. Nothing uses it until it is published, and publishing needs a second person, the checker. The person who made it can never approve it. That is called maker and checker.',
      do: async () => { await save(); await publish(); },
    },
    {
      say: 'A rule does not run on its own. It goes into a rule set: a list of rules that run together, in stages. A rule set is a short document, so we write it in the YAML pane: its name, its stage, field, and its one rule, ours.',
      do: async () => {
        await pasteDoc('ruleset', D.MONTH_RULESET);
        await d.point(main().getByText(D.MONTH_RULE.name).first(), 2500);
      },
    },
    {
      say: 'Saved, and published by the checker.',
      do: async () => { await save(); await publish(); },
    },

    // ------------------------------------------------------------ part 2: an activity
    {
      card: ['Part 2: an activity', 'A station that does one job, tested on the bench'],
      say: 'Part two: an activity. If a rule is a quality check, an activity is a station in the kitchen: one job, with a clear way in and a clear way out. Our station runs the salary month check.',
      do: async () => { await startNew('activity'); },
    },
    {
      say: 'A name and a title. The type is validate, because it checks things. The level is batch, the whole file. The effect is pure: it only looks, and changes nothing outside.',
      do: async () => {
        await d.type(p().getByLabel('Name', { exact: true }), D.MONTH_ACTIVITY.name, 40);
        await d.type(p().getByLabel('Title', { exact: true }), D.MONTH_ACTIVITY.title, 40);
        await d.select(p().getByLabel('Activity type'), 'validate');
        await d.select(p().getByLabel('Activity level'), 'batch');
        await d.select(p().getByLabel('Effect'), 'pure');
      },
    },
    {
      say: 'Reads are what the station is allowed to see. It only needs the W P S part of the file, so we remove the sample reads and add that one. Writes are what it may change: here, only its findings.',
      do: async () => {
        await removeChip('header');
        await removeChip('payment');
        await addChip('Add a read', 'ext.uae.wps');
        await d.point(main().getByText('Writes').first(), 1500);
      },
    },
    {
      say: 'The outcomes are the ways out: pass, the normal way, and reject. The workflow that uses this station must say what to do on reject. And the station runs our rule set, through the studio\'s built in rule service.',
      do: async () => {
        await d.point(main().getByText('Outcomes').first(), 1800);
        await addChip('Add a rule set', D.MONTH_RULESET.name);
        await d.point(p().getByLabel('Binding'), 1500);
      },
    },
    {
      say: 'Saved, and published.',
      do: async () => { await save(); await publish(); },
    },
    {
      say: 'Now we test the activity on the bench. The bench runs one thing on its own, with made up input. We choose our activity as the target, and give it a small context: a file whose salary month is October.',
      do: async () => {
        await bench(D.MONTH_ACTIVITY.name, 'owner', []);
        await withContext(D.contextWithMonth('102026'));
      },
    },
    {
      say: 'Run. The outcome is pass, and the business trace says so: validate, pass.',
      do: async () => { await run(/validate:pass/); },
    },
    {
      say: 'Now month thirteen. Run again. This time the outcome is reject.',
      do: async () => {
        await main().getByText('Input').first().scrollIntoViewIfNeeded();
        await withContext(D.contextWithMonth('132026'));
        await run(/validate:reject/);
      },
    },
    {
      say: 'A run you like can become a test that is kept. Below the result, save the run as a test: give it an id and a title. From now on, if anyone changes the rule and this result changes, the test turns red.',
      do: async () => {
        await main().getByText('Save the run as a test').first().scrollIntoViewIfNeeded();
        await d.type(p().getByLabel('Test id'), 'T-TRAIN-MONTH-13', 40);
        await d.type(p().getByLabel('Test title'), 'Month thirteen is rejected', 40);
        await d.click(p().getByRole('button', { name: /^Save as a test draft as / }));
        await d.wait(1200);
      },
    },

    // ------------------------------------------------------------ part 3: the AI assistant
    {
      card: ['Part 3: the AI assistant', 'A first draft from a template, judged by its behaviour'],
      say: 'Part three: the AI assistant. It writes a first draft for you, from the text of a rulebook. It never publishes anything. Its draft goes through the same gates as yours, and the same maker and checker.',
      do: async () => { await d.go('assistant'); await d.point(main().getByText('Status').first().locator('..'), 2500); },
    },
    {
      say: 'We ask for a rule. The easiest start is a prompt template. We choose field check. The template is a sentence with blanks in angle brackets, and the studio will not send it until every blank is filled.',
      do: async () => {
        await d.select(p().getByLabel('Draft kind'), 'rule');
        await d.select(p().getByLabel('Prompt template'), 'rule-field');
        await expectText(/Still to fill/);
        await d.point(main().getByText('Still to fill').first(), 2500);
      },
    },
    {
      say: 'We fill the blanks: the name, the level, the value it checks, days in the pay period, which must be from one to thirty one, and the reason code. Then the source text, the part of the rulebook it should follow. Here a made up extract.',
      do: async () => {
        await d.paste(p().getByLabel('Instruction'), D.AI_INSTRUCTION);
        await d.paste(p().getByLabel('Source text'), D.AI_SOURCE);
        await d.point(p().getByRole('button', { name: 'Ask for a draft' }), 1500);
      },
    },
    AI_LIVE ? {
      say: 'We ask for the draft. It comes back with the rule, its examples, the clauses it used, and questions about anything the text did not say. The gates have already run on it.',
      do: async () => {
        await d.click(p().getByRole('button', { name: 'Ask for a draft' }));
        await expectText(/every gate green|red at/);
        await main().getByText('Draft').first().scrollIntoViewIfNeeded();
        await d.wait(2500);
      },
    } : {
      say: 'We ask for the draft. In this recording the studio has no model key, so it says so plainly. It never invents a draft in place of a real one. With a key set for the server, the rule comes back with its examples, the clauses it used, and its questions.',
      do: async () => {
        await d.click(p().getByRole('button', { name: 'Ask for a draft' }));
        await d.wait(2000);
        await d.point(main().locator('.alert').first(), 2500);
      },
    },
    {
      say: 'Whatever the assistant writes, you test it exactly like the rule we built by hand. Read the examples. Add the edge case, here day one and day thirty one. Break one on purpose. Answer its questions. Only then save it, and a checker publishes it.',
      do: async () => { await d.scroll(-400); await d.point(main().getByText('What comes back').first().locator('..'), 3000); },
    },

    // ------------------------------------------------------------ part 4: compose from existing blocks
    {
      card: ['Part 4: a workflow from parts that exist', 'Composing a leg on the canvas'],
      say: 'Part four: a workflow, built only from parts that already exist. This is a recipe made from stations the kitchen already has. Our recipe is a W P S salary file leg, the scheme\'s part of a payment, with our new salary month check added.',
      do: async () => { await d.go('designer'); },
    },
    {
      say: 'We start a blank leg and paste its header. The header says three things: it fills the scheme leg slot of the corporate payment workflow, what it may read, and how it can end: settled, rejected, returned or pending.',
      do: async () => { await designer('workflow', 'leg', D.COMPOSED_LEG_HEADER); },
    },
    {
      say: 'On the canvas, the recipe has one step: end, settled. We insert a step before it. The palette offers the kinds of step. Call means: use a station. We search the catalogue for business date, and pick the block that sets the business date. We name the step.',
      do: async () => { await canvasCall(0, 'business-date', 'business-date', 'uae.p.enrich.business-date.v1', 0); },
    },
    {
      say: 'The next station checks the whole salary file. It has two other ways out, repair and reject, and each one shows red until we say what to do. For both, we end the workflow as rejected.',
      do: async () => { await canvasCall(1, 'file-checks', 'salary-file', 'uae.wps.validate.salary-file.v1', 2); },
    },
    {
      say: 'Then our own new station, the salary month check, from part two. If it rejects, the workflow ends as rejected.',
      do: async () => {
        await canvasCall(2, 'month-check', 'salary-month', D.MONTH_ACTIVITY.name, 1);
        const y: string = await p().getByLabel('Workflow YAML').inputValue();
        for (const want of ['id: business-date', 'id: file-checks', 'id: month-check', 'end: rejected', `call: ${D.MONTH_ACTIVITY.name}`]) if (!y.includes(want)) throw new Error(`The canvas did not write ${want}:\n${y}`);
      },
    },
    {
      say: 'The rest is built the same way: for each line of the file, check the record and its IBAN; render the file for the scheme; submit it and wait for the answer. To save time, we paste the finished steps.',
      do: async () => { await d.paste(p().getByLabel('Workflow YAML'), toYaml(D.COMPOSED_LEG)); await d.wait(1500); await d.scroll(300); },
    },
    {
      say: 'The checks under the canvas say there is no error: every outcome is handled, and every block exists and is published. We save the leg and the checker publishes it.',
      do: async () => { await expectText(/no errors/); await save(); await publish(); },
    },
    {
      say: 'Now we taste the recipe on the bench. The leg runs against simulators: stand ins for the W P S system and the bank calendar. First a good file with two lines.',
      do: async () => {
        await bench(D.COMPOSED_LEG.name, 'owner', D.WPS_PORTS);
        await withSample('samples/wps-sif-two-lines.sif');
      },
    },
    {
      say: 'Run. Each station in order: business date, file checks, our month check passes, both lines pass, the file is rendered and accepted. The leg settles.',
      do: async () => { await run(/month-check:pass/); },
    },
    {
      say: 'Now a file with month thirteen. Run. The file checks pass, but our month check rejects it, and the leg ends as rejected. Nothing was sent to the scheme.',
      do: async () => {
        await main().getByText('Input').first().scrollIntoViewIfNeeded();
        await withSample('samples/wps-sif-bad-month.sif');
        await run(/month-check:reject/);
      },
    },

    // ------------------------------------------------------------ part 5: when nothing exists
    {
      card: ['Part 5: when nothing exists', 'A new scheme from scratch, in order'],
      say: 'Part five: when nothing exists. For a new scheme, there are no stations, no checks and no door yet. You build them, in an order where each part only uses parts already built.',
      do: async () => { await d.go('catalogue'); },
    },
    {
      say: 'The order is: sample files first, so you can test. Then the port, the door to the scheme, and its simulator, the stand in. Then the rules, the rule set and the check activity. Then the maps that translate files. Then the send, and the composite that sends and waits. Then the leg. Last, the scheme and its dispatch table.',
      do: async () => { await d.go('catalogue'); await d.point(main().locator('table').first(), 4000); },
    },
    {
      say: 'The dispatch table is the scheme\'s routing map. It says: this kind of file starts an outward corporate payment, read by this intake map, with this leg in the slot. Our first demo video builds such a scheme, step by step, and the guide lists every click.',
      do: async () => { await d.go('editors?kind=scheme'); await d.point(main().getByText('Scheme and dispatch table').last(), 3500); },
    },

    // ------------------------------------------------------------ part 6: a bank's own rules and hooks
    {
      card: ['Part 6: a bank\'s own rules and hooks', 'The franchise adds its own touch, without changing the recipe'],
      say: 'Part six: a bank\'s own touch. The shared recipes belong to the platform. A bank cannot change them. But it can set its own values, and plug its own steps into hook points: places the recipe leaves open for it.',
      do: async () => { await d.go('tenant'); await d.as('example-bank', 'example.maker'); await d.go('tenant'); },
    },
    {
      say: 'We now act as the example bank, as its maker. Here is what it may set: parameters with their bounds, and the hook points it may use. One hook point sits right after a payment is validated. A bank can hold or reject a valid payment there.',
      do: async () => { await d.point(main().getByText('Hook points').first().locator('..'), 3500); },
    },
    {
      say: 'The bank\'s policy: a salary file with more lines than it allows needs a person to approve it. First the rule. It has a parameter, max lines, with a default and bounds, so each bank sets its own value. And three examples: under, exactly at, and over the limit.',
      do: async () => {
        await pasteDoc('rule', D.BANK_RULE);
        await main().getByText('Parameters').first().scrollIntoViewIfNeeded();
        await d.wait(1500);
        await main().getByText('Examples').first().scrollIntoViewIfNeeded();
        await d.wait(1500);
      },
    },
    {
      say: 'The bank\'s own maker saves it, and the bank\'s own checker publishes it. Its name starts with b, the bank\'s scope.',
      do: async () => { await save(); await publish('example.checker'); },
    },
    {
      say: 'The same steps as before: a rule set with the rule, and a check activity that runs it.',
      do: async () => {
        await pasteDoc('ruleset', D.BANK_RULESET); await save(); await publish('example.checker');
        await pasteDoc('activity', D.BANK_ACTIVITY); await save(); await publish('example.checker');
      },
    },
    {
      say: 'Now the hook: a small recipe that plugs into the hook point. It runs the file size check. If the check rejects, the hook ends as hold, and the payment waits for a person. Otherwise it ends as continue.',
      do: async () => { await designer('hook', undefined, D.BANK_HOOK); await d.point(main().locator('.cv-steps').first(), 2500); },
    },
    {
      say: 'Saved and published by the bank\'s checker.',
      do: async () => { await save(); await publish('example.checker'); },
    },
    {
      say: 'Now the bank\'s configuration. We bind the hook to the hook point, and set max lines. Watch the bounds: if we type zero, below the minimum of one, the check refuses it, and the save button stays off.',
      do: async () => {
        await d.go('tenant');
        await tenantYaml(bankConfig(0));
        await expectText(/L14/);
        await d.point(main().getByText('L14').first(), 2500);
      },
    },
    {
      say: 'For training we set it to one line, so our two line sample file goes over the limit. The set passes every check. We save it as a new release of the configuration.',
      do: async () => {
        await tenantYaml(bankConfig(1));
        await expectText(/passes every check/);
        await d.click(p().getByRole('button', { name: 'Save the configuration set' }));
        await d.wait(1200);
      },
    },
    {
      say: 'Before a bank accepts a new shared release, it runs the compatibility run: each bound hook point must still exist and fit, and each of its hook tests must pass.',
      do: async () => {
        await d.click(main().getByText('Compatibility run').first().locator('..').getByRole('button', { name: 'Run' }));
        await expectText(/may accept|do not accept/);
        await d.point(main().getByText('Hook bindings').first().locator('..'), 3000);
      },
    },
    {
      say: 'Now a whole payment, for the bank. The salary file arrives from the bank\'s channel. The operations simulator plays the person who decides a hold. In this run, they decline.',
      do: async () => {
        await bench('p.wf.corporate.v1', 'example-bank', [...PLATFORM, ...D.WPS_PORTS, ['p.port.operations-queue.v1', 'p.sim.operations.v1', 'decline']]);
        await withSample('samples/wps-sif-two-lines.sif', 'uae.wps.sif');
      },
    },
    {
      say: 'Run. The payment is validated. Then our hook runs, finds two lines over a limit of one, and holds it. The person declines, so the payment ends as rejected, and nothing was sent.',
      do: async () => { await run(/hold:declined/); },
    },
    {
      say: 'Once more, and this time the person approves. The hold is released, the payment is screened, the funds are reserved, the W P S leg runs, and the payment settles.',
      do: async () => {
        await main().getByText('Ports and simulators').first().scrollIntoViewIfNeeded();
        await d.select(p().getByLabel('Scenario', { exact: true }).last(), 'approve');
        await run(/hold:approved/);
      },
    },
    {
      card: ['That is the hands-on training', 'Rule, activity, assistant, composing, building, and the bank\'s own touch'],
      say: 'That is the end of the hands-on training. You built a rule and tested it with examples. An activity, tested on the bench. A draft from the assistant. A leg composed from existing parts. And a bank\'s own rule and hook, held a payment for approval. The guide has every step in writing.',
      do: async () => { await d.wait(1500); await d.go('catalogue'); },
    },
  ];
}
