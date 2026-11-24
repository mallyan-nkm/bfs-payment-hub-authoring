import { toYaml } from '../src/core/yaml.js';
import * as D from './designer/docs.js';
import { SLOW, type Director, type Scene } from './record.js';

// Video 5b, one per console: the studio as the platform's designer and developer. The catalogue and the factory's scopes; the ISO
// data dictionary; a rule with its examples; the workflow canvas, with its blocks, subflows, slots and legs; schemes and the
// dispatch table; ports and simulators; then a new activity that needs a script, written in full: its contract, its TypeScript
// script-behind in the studio's editor, its tests on the bench, run as stored tests, the activity used in a leg on the canvas,
// and the leg run on the bench and in the debugger; the explain panel and the inline assistant; import; the export to the four
// engines; the conformance kit; and governance and publishing. Every value is synthetic. With TH_DEMO_DRY=1 the same steps run
// fast, with no video, and each expected result is checked.

export const NAME = '5b-studio-designer-developer';
export const TITLE = 'TransactHub Designer Studio, 5b: the designer and developer';
export const PACE = SLOW;

const DESIGNER = 'shared.designer';
const CHECKER = 'shared.checker';
// The same rule as the studio server's (assistantConfigured); a dry run never calls a model.
const AI_LIVE = process.env.TH_DEMO_DRY !== '1' && !!(process.env.ANTHROPIC_API_KEY || process.env.TH_ANTHROPIC_API_KEY || process.env.ANTHROPIC_AUTH_TOKEN);

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');
  const panel = (title: RegExp) => main().locator('.panel', { has: p().locator('h3', { hasText: title }) }).first();

  // ---------------------------------------------------------------- helpers

  const status = async (re: RegExp) => {
    for (let i = 0; i < 160; i++) {
      if ((await p().locator('[role="status"]').allTextContents()).some((t: string) => re.test(t))) return;
      await p().waitForTimeout(250);
    }
    throw new Error(`No status matching ${re}`);
  };
  /** Wait until the page shows the text; a step that does not produce it stops the recording. */
  const expectText = async (re: RegExp, seconds = 30, where: any = main()) => {
    for (let i = 0; i < seconds * 4; i++) {
      if (re.test(await where.innerText())) return;
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
  /** Scroll a part of the page to the middle of the screen, above the captions. */
  const centre = async (loc: any) => {
    await loc.first().evaluate((e: any) => e.scrollIntoView({ block: 'center' }));
    await d.wait(500);
  };
  const dock = () => p().getByRole('complementary', { name: 'Assistant' });
  const dbgBar = () => p().getByRole('toolbar', { name: 'Debugger controls' });
  const expectDbg = async (re: RegExp) => {
    for (let i = 0; i < 40; i++) {
      if (re.test((await p().getByTestId('dbg-now').textContent()) ?? '')) return;
      await p().waitForTimeout(150);
    }
    throw new Error(`The debugger does not show ${re}: ${await p().getByTestId('dbg-now').textContent()}`);
  };
  const startNew = async (kind: string) => {
    await d.go(`editors?kind=${kind}`);
    await d.click(p().getByRole('button', { name: 'Start new' }));
  };
  const save = async () => {
    await d.click(p().getByRole('button', { name: /^Save draft$/ }).first());
    await status(/Saved draft/);
  };
  const publish = async () => {
    await d.release(CHECKER, DESIGNER);
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
  const bench = async (target: string, ports: [string, string, string?][]) => {
    await d.go('tests');
    await d.click(p().getByRole('tab', { name: 'Ad hoc bench' }));
    await d.select(p().getByLabel('Target', { exact: true }), target);
    for (const [port, sim, scenario] of ports) {
      await d.click(p().getByRole('button', { name: 'Add a port' }));
      await d.select(p().getByLabel('Port', { exact: true }).last(), port);
      await d.select(p().getByLabel('Simulator', { exact: true }).last(), sim);
      await d.select(p().getByLabel('Scenario', { exact: true }).last(), scenario ?? 'accept');
    }
  };
  const withContext = async (json: string) => {
    await d.click(p().getByRole('tab', { name: 'Context as JSON' }));
    await d.paste(p().getByLabel('Context JSON'), json);
  };
  const withSample = async (sample: string) => {
    await d.click(p().getByRole('tab', { name: 'Inbound sample' }));
    await d.select(p().getByLabel('Inbound sample'), sample);
  };
  const run = async (expect: RegExp) => {
    await d.click(p().getByRole('button', { name: 'Run the bench' }));
    await expectText(expect, 60);
    await centre(main().getByText('Business trace'));
    await d.wait(900);
  };
  const saveTest = async (id: string, title: string) => {
    await centre(main().getByText('Save the run as a test'));
    await d.type(p().getByLabel('Test id'), id, 40);
    await d.type(p().getByLabel('Test title'), title, 30);
    await d.click(p().getByRole('button', { name: /^Save as a test draft as / }));
    await status(/Saved the test draft/);
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
  /** In Governance: the designer requests the release of each draft that matches, and the checker approves each. */
  const releaseDrafts = async (match: RegExp) => {
    await d.go('governance');
    for (let i = 0; i < 6; i++) {
      const b = main().locator('tr', { hasText: match }).locator('button:not([disabled])', { hasText: 'Request release' });
      if (!(await b.count())) break;
      await d.click(b.first());
      await status(/Release requested/);
    }
    await d.as('', CHECKER);
    await d.go('governance');
    for (let i = 0; i < 6 && (await main().locator('tr', { hasText: match }).count()); i++) await d.approveRow(match.source.replace(/\\/g, ''));
    await d.as('', DESIGNER);
  };

  // ---------------------------------------------------------------- the scenes

  return [
    {
      card: ['5b. The designer and developer', 'The studio as the platform\'s designer and developer, every feature'],
      say: 'This video shows the studio as the platform\'s designer and developer use it. They write the shared content every bank runs: the data, the rules, the activities, the workflows, the schemes and their connections. And where a form is not enough, a developer writes a script. Every value you will see is made up for training.',
      do: async () => { await d.go('catalogue'); },
    },
    {
      say: 'We are signed in as the workflow designer. The top bar says shared content: what we write here is the platform\'s, not one bank\'s. The left bar lists the designer\'s surfaces: the designer, the editors, the catalogue, the data, the assistant, import, the test bench, the conformance kit, export and governance.',
      do: async () => {
        await d.point(p().locator('header .topbar-badge').first(), 2000);
        await d.point(p().locator('nav').first(), 3500);
      },
    },

    // ------------------------------------------------------------------ 1. the catalogue and the factory
    {
      card: ['1. The catalogue and the factory', 'Scopes, names and releases'],
      say: 'The catalogue lists every published artefact. The factory is the content the platform ships, in scopes. p is the platform, shared by every scheme. U A E p is the U A E cluster\'s platform. And U A E W P S is the salary scheme. A scheme may use its cluster and p. p uses nothing else.',
      do: async () => {
        await d.point(main().getByLabel('Scope'), 2000);
        await d.select(main().getByLabel('Scope'), 'uae.p');
        await d.wait(800);
        await d.select(main().getByLabel('Scope'), 'uae.wps');
        await d.point(main().locator('table').first(), 2500);
      },
    },
    {
      say: 'Every name has four parts: the scope, the type, the name, and the major version. U A E W P S, validate, salary file, v one. A release inside the major is three numbers. A published release is never edited: a change is a new release.',
      do: async () => {
        await d.select(main().getByLabel('Kind'), 'activity');
        await d.wait(800);
        await d.point(main().locator('table tbody tr').first(), 3000);
      },
    },
    {
      say: 'Search finds an artefact by name or by the data it touches. Here, iban. The activity page says what the block reads and writes, its outcomes, its releases, who uses it, what it uses, and the tests that target it.',
      do: async () => {
        await d.select(main().getByLabel('Scope'), '');
        await d.select(main().getByLabel('Kind'), '');
        await d.type(main().getByRole('searchbox', { name: 'Search' }), 'iban', 100);
        await d.click(p().locator('a[href="#/artefact/uae.p.validate.iban.v1"]').first());
        await d.wait(800);
        await d.point(panel(/^Contract/), 2500);
        await centre(panel(/^Used by/));
        await d.point(panel(/^Used by/), 2000);
      },
    },

    // ------------------------------------------------------------------ 2. the ISO data dictionary
    {
      card: ['2. The ISO data dictionary', 'One canonical model, ISO 20022'],
      say: 'Every workflow works on one context, the canonical model, in ISO 20022 terms. Only a map touches another format. The ISO data page lists every path, with the blocks that read it and the blocks that write it. Here, the instructed amount.',
      do: async () => {
        await d.go('data');
        await d.type(main().getByRole('searchbox', { name: 'Search the data' }), 'payment.instructedAmount', 50);
        await d.wait(800);
        await d.point(panel(/^Paths/), 3000);
      },
    },
    {
      say: 'The canonical model, as a tree, with each element\'s ISO type. A field a scheme needs that ISO does not have is a registered extension, such as the W P S salary fields. There is no untyped field.',
      do: async () => {
        await d.click(p().getByRole('tab', { name: 'Canonical model' }));
        await p().waitForTimeout(1000);
        await d.point(panel(/^The context/), 3000);
      },
    },
    {
      say: 'The code registry holds the reason codes, from the ISO code sets, and says which blocks give each one. And the sample messages: for every scheme message, a valid sample and invalid ones, each proven against its checks.',
      do: async () => {
        await d.click(p().getByRole('tab', { name: 'Code registry' }));
        await p().waitForTimeout(1200);
        await d.point(main().locator('.panel').first(), 2500);
        await d.click(p().getByRole('tab', { name: 'Sample messages' }));
        await p().waitForTimeout(1200);
        await d.point(main().locator('.panel').first(), 2500);
      },
    },

    // ------------------------------------------------------------------ 3. rules and their examples
    {
      card: ['3. Rules and their examples', 'A quality check, tested as you write it'],
      say: 'A rule is one check that answers yes or no. Ours checks the salary month on a W P S file: six digits, month and year, and month thirteen does not exist. The rule editor is a form. We give the rule its name and title.',
      do: async () => {
        await startNew('rule');
        await d.type(p().getByLabel('Name', { exact: true }), D.MONTH_RULE.name, 40);
        await d.type(p().getByLabel('Title', { exact: true }), D.MONTH_RULE.title, 40);
        await d.select(p().getByLabel('Rule kind'), 'field');
        await d.select(p().getByLabel('Rule level'), 'batch');
      },
    },
    {
      say: 'When: the fact it looks at, the W P S batch, called w. Where: the salary month does not match a real month. Then: reject the file, with the path of the wrong value and the reason code D T zero one, an invalid date.',
      do: async () => {
        await d.type(main().locator('.cond-row').first().getByLabel('Fact'), 'WpsBatch', 60);
        await d.type(main().locator('.cond-row').first().getByLabel('Binds as'), 'w', 60);
        await d.click(main().locator('.cond-row').nth(1).getByRole('button', { name: 'Remove' }));
        await d.type(main().locator('.cond-row').first().getByLabel('Where'), D.MONTH_RULE.where, 25);
        await d.select(p().getByLabel('Severity', { exact: true }), 'reject');
        await d.type(p().getByLabel('Finding path', { exact: true }), D.MONTH_RULE.path, 40);
        await d.type(p().getByLabel('Reason code', { exact: true }), D.MONTH_RULE.code, 80);
      },
    },
    {
      say: 'Now its examples, each a small test. October passes. Month thirteen fails with D T zero one. And the edge: December, the last real month, passes. The gates on the right run on every change: the schema, the static checks, and every example.',
      do: async () => {
        await centre(main().getByText('Examples'));
        await d.paste(p().getByLabel('Given of passes'), JSON.stringify({ WpsBatch: { salaryMonth: '102026' } }, null, 1));
        await d.paste(p().getByLabel('Given of fails'), JSON.stringify({ WpsBatch: { salaryMonth: '132026' } }, null, 1));
        await removeChip('NARR');
        await addChip('Expected reason codes of fails', D.MONTH_RULE.code);
        await d.click(p().getByRole('button', { name: 'Add an example' }));
        await d.paste(p().getByLabel('Given of example 3'), JSON.stringify({ WpsBatch: { salaryMonth: '122026' } }, null, 1));
        await d.type(main().getByLabel('Example name').nth(2), 'boundary, December', 40);
        await expectCount(main().locator('.example.ex-pass'), 3);
        await d.point(main().locator('.col.side').first(), 2500);
      },
    },
    {
      say: 'Break it on purpose: give the passing example month zero zero. The rule rejects it, so that example turns red and shows what really happened. Here the example was wrong, not the rule, so we put October back. Every example is green again.',
      do: async () => {
        await d.paste(p().getByLabel('Given of passes'), JSON.stringify({ WpsBatch: { salaryMonth: '002026' } }, null, 1));
        await expectCount(main().locator('.example.ex-fail'), 1);
        await d.point(main().locator('.example').first(), 2500);
        await d.paste(p().getByLabel('Given of passes'), JSON.stringify({ WpsBatch: { salaryMonth: '102026' } }, null, 1));
        await expectCount(main().locator('.example.ex-pass'), 3);
      },
    },
    {
      say: 'We save the draft and request its release. A checker, another person, approves it as a version. Rules also come as decision tables, rows of when and then, in their own editor, with a check for gaps and overlaps.',
      do: async () => {
        await save();
        await publish();
        await d.go('editors?kind=table&open=p.table.rail-choice.v1');
        await expectText(/rail-choice/);
        await centre(panel(/^Rows/));
        await d.point(panel(/^Rows/), 2500);
      },
    },

    // ------------------------------------------------------------------ 4. the workflow canvas
    {
      card: ['4. The workflow canvas', 'Blocks, subflows, slots and legs'],
      say: 'The workflow designer. We open the corporate payment workflow, the platform\'s workflow for an outward file. On the left is its text, on the right the same workflow drawn as blocks. The two stay in step, and every check runs on every edit.',
      do: async () => {
        await d.go('designer');
        await d.select(p().getByLabel('Published workflow'), { label: 'p.wf.corporate.v1 @1.0.0' });
        await d.click(p().getByRole('button', { name: 'Open', exact: true }).last());
        await d.wait(1200);
        await centre(main().locator('.cv-steps').first());
        await d.point(main().locator('.cv-steps').first(), 2500);
      },
    },
    {
      say: 'Each block is a call to an activity, with what to do on each outcome that is not the main one. Its interface names a slot, scheme leg, which a scheme fills, and two hook points, before release and on close, where a bank plugs in its own steps.',
      do: async () => {
        await d.point(main().getByText(/Interface: outcomes/).first(), 3500);
        await d.scroll(500);
        await d.wait(1500);
      },
    },
    {
      say: 'The outline shows the steps as a list. The expanded form is what the engines run: each composite step opened into its subflow, and nobody edits it by hand.',
      do: async () => {
        await centre(p().getByRole('tab', { name: 'Outline' }));
        await d.click(p().getByRole('tab', { name: 'Outline' }));
        await d.wait(2000);
        await d.click(p().getByRole('tab', { name: 'Expanded form' }));
        await d.wait(2000);
      },
    },
    {
      say: 'A composite is one step in a workflow that does several things underneath: here, submit the salary file and wait for the scheme\'s answer. The wait lives in its subflow, shown below the composite\'s contract, on a canvas of its own.',
      do: async () => {
        await d.go('editors?kind=composite&open=uae.wps.exchange.submit-salary-file.v1');
        await expectText(/Subflow/);
        await centre(main().locator('h3.sub-head'));
        await d.wait(3000);
      },
    },
    {
      say: 'A leg is the scheme\'s part of a payment. The W P S salary file leg fills the corporate workflow\'s slot: it checks the file and each line, renders the file for the scheme, submits it, and ends as settled, rejected, returned or pending.',
      do: async () => {
        await d.go('designer?open=uae.wps.wf.salary-file-out.v1');
        await expectText(/salary-file-out/);
        await d.wait(1200);
        await centre(main().locator('.cv-steps').first());
        await d.point(main().locator('.cv-steps').first(), 3000);
      },
    },

    // ------------------------------------------------------------------ 5. schemes and the dispatch table
    {
      card: ['5. Schemes and the dispatch table', 'Which message starts which workflow'],
      say: 'A scheme\'s manifest holds its dispatch table: for each message, what to do with it. A salary file starts an outward corporate payment, read by its intake map, with the W P S leg in the slot. An answer from the scheme is delivered to the workflow that awaits it.',
      do: async () => {
        await d.go('editors?kind=scheme&open=' + encodeURIComponent('scheme:uae.wps'));
        const table = panel(/^Scheme and dispatch table/);
        await centre(table.locator('tr', { hasText: 'uae.wps.sif' }));
        await d.point(table.locator('tr', { hasText: 'uae.wps.sif' }).first(), 4000);
      },
    },

    // ------------------------------------------------------------------ 6. ports and simulators
    {
      card: ['6. Ports and simulators', 'A door to another system, and its stand-in'],
      say: 'A port is a door to another system, here the W P S system: its party, and its operations, to send a file and to receive one. The bank decides later which adapter answers it.',
      do: async () => {
        await d.go('editors?kind=port');
        await d.select(p().getByLabel('Open a published Port'), 'uae.wps.port.wps.v1');
        await d.wait(1200);
        await d.point(panel(/^Port/), 3000);
      },
    },
    {
      say: 'Every port has a simulator, a stand-in for the bench and the tests. It must carry the eight standard scenarios: accept, reject, no reply, a late reply, a duplicate, and more. A simulator is never part of a package a bank promotes.',
      do: async () => {
        await d.go('editors?kind=simulator');
        await d.select(p().getByLabel('Open a published Simulator'), 'uae.wps.sim.wps-outward.v1');
        await d.wait(1200);
        await d.point(panel(/^Standard scenarios/), 3500);
      },
    },

    // ------------------------------------------------------------------ 7. an activity with a script
    {
      card: ['7. Writing an activity with a script', 'Its contract, its script-behind, its tests, and its use'],
      say: 'Now the developer\'s part. Some checks are more than a rule. W P S pays each employee for a pay period, with a start date, an end date, and the days paid. Our new activity checks that each line\'s period lies inside the file\'s salary month, starts before it ends, and pays the right number of days.',
      do: async () => { await startNew('activity'); },
    },
    {
      say: 'First the contract: what the activity is, before how it works. Its name in the W P S scope, a title, and a description. Its type is validate, because it checks. Its level is batch: it sees the whole file at once.',
      do: async () => {
        await d.type(p().getByLabel('Name', { exact: true }), D.PERIOD_ACTIVITY.name, 35);
        await d.type(p().getByLabel('Title', { exact: true }), D.PERIOD_ACTIVITY.title, 25);
        await d.type(p().getByLabel('Description', { exact: true }), D.PERIOD_ACTIVITY.description, 12);
        await d.select(p().getByLabel('Activity type'), 'validate');
        await d.select(p().getByLabel('Activity level'), 'batch');
      },
    },
    {
      say: 'What it may read: the W P S salary fields of the file, and its lines. What it may write: its findings, and nothing else. A script that touches anything else is stopped. Its outcomes: pass, the main one, and reject, which every workflow that uses it must handle.',
      do: async () => {
        await removeChip('header');
        await removeChip('payment');
        await addChip('Add a read', 'ext.uae.wps');
        await addChip('Add a read', 'items[]');
        await d.point(main().getByText('Writes').first(), 1500);
        await d.point(main().getByText('Outcomes').first(), 1500);
      },
    },
    {
      say: 'Its implementation is a script, in TypeScript. A check without rule sets is declared with effect read. The form is complete. And the gates say what is missing: the schema and the static checks are green, but no test yet expects pass or reject.',
      do: async () => {
        await d.select(p().getByLabel('Effect'), 'read');
        await centre(panel(/^Implementation/));
        await d.select(p().getByLabel('Binding', { exact: true }), 'script');
        await d.select(p().getByLabel('Language', { exact: true }), 'typescript');
        await d.type(p().getByLabel('Script', { exact: true }), D.PERIOD_ACTIVITY.implementation.script, 20);
        await centre(panel(/^Gates/));
        await d.point(panel(/^Gates/), 2500);
      },
    },
    {
      say: 'We save the contract as a draft. Now the script-behind editor appears. A script sees only what the studio gives it: the context, ports, rules, parameters, a clock, ids and a log. Nothing else.',
      do: async () => {
        await save();
        await centre(panel(/^Script-behind/));
        await d.point(panel(/^Script-behind/), 3000);
      },
    },
    {
      say: 'A first try. We start by reading today\'s date from the computer, and save. The studio refuses it: the script uses new Date, outside the injected interface. A script never reads the system clock, randomness, the network, files or the environment, so that every run of it gives the same answer.',
      do: async () => {
        await d.type(p().getByLabel('Script source'), D.PERIOD_SCRIPT_CLOCK, 30);
        await d.click(p().getByRole('button', { name: /^Save the script as / }));
        await status(/new Date/);
        await d.point(panel(/^Script-behind/).getByRole('status'), 3000);
      },
    },
    {
      say: 'Now the real script. It reads the salary month, month then year. For each line, it takes the start and end dates, and checks that both fall in that month, and that the start is not after the end. Dates are ISO text, so this needs no clock at all.',
      do: async () => {
        const cut = D.PERIOD_SCRIPT.indexOf('    const days');
        await d.type(p().getByLabel('Script source'), D.PERIOD_SCRIPT.slice(0, cut), 14);
      },
    },
    {
      say: 'Then the days paid must be the days from start to end, both included. Each problem is a finding with a path, a reason code and a message. The findings are written to the context, a line goes to the log, and the outcome is reject when there is a finding, else pass.',
      do: async () => {
        const src = p().getByLabel('Script source');
        const cut = D.PERIOD_SCRIPT.indexOf('    const days');
        await src.press('End');
        if (d.dry) await src.fill(D.PERIOD_SCRIPT);
        else await src.pressSequentially(D.PERIOD_SCRIPT.slice(cut), { delay: 14 });
        await d.wait(600);
      },
    },
    {
      say: 'Save the script. This time it passes the check, and is saved beside the activity\'s draft, in the same change set. The generated types give a developer the exact shape of what the script reads and writes.',
      do: async () => {
        await d.click(p().getByRole('button', { name: /^Save the script as / }));
        await status(/Saved uae\.wps\.validate\.pay-period\.v1\.ts/);
        await d.point(panel(/^Script-behind/).getByRole('status'), 2500);
        await d.point(main().getByRole('link', { name: /See the generated types/ }), 2000);
      },
    },
    {
      say: 'The bench runs published releases, so the checker approves the activity and its script now. Its tests follow in a change set of their own. Once published, its behaviour is fixed: a change to the script is a new release.',
      do: async () => { await publish(); },
    },
    {
      say: 'On the bench, we choose our activity and give it a small context: a salary file for October, with two lines, each paid for the whole month, thirty one days.',
      do: async () => {
        await bench(D.PERIOD_ACTIVITY.name, []);
        await withContext(D.periodContext(31));
      },
    },
    {
      say: 'Run. The outcome is pass. We keep this run as a test: an id, a title, and the studio takes the assertions from the run.',
      do: async () => {
        await run(/validate:pass/);
        await saveTest('T-PAY-PERIOD-001', 'Lines paid for the whole salary month pass');
      },
    },
    {
      say: 'Now the second line pays thirty days for a thirty one day period. Run. The outcome is reject. We keep it as a second test. Now each outcome has a test.',
      do: async () => {
        await centre(main().getByText('Input'));
        await withContext(D.periodContext(30));
        await run(/validate:reject/);
        await saveTest('T-PAY-PERIOD-002', 'A line paid for the wrong number of days is rejected');
      },
    },
    {
      say: 'The two tests are drafts too. In Governance we request their release, and the checker approves them. From now on, a change that breaks either one stops the approval.',
      do: async () => { await releaseDrafts(/T-PAY-PERIOD/); },
    },
    {
      say: 'On the test bench\'s tests tab we find them and run them. Both pass. Every stored test runs the same way, alone or all together.',
      do: async () => {
        await d.go('tests');
        await d.type(p().getByLabel('Filter tests'), 'T-PAY-PERIOD', 60);
        for (const id of ['T-PAY-PERIOD-001', 'T-PAY-PERIOD-002']) {
          await d.click(main().locator('tr', { hasText: id }).first().getByRole('button', { name: 'Run' }));
          await expectText(/pass/);
        }
        await d.point(main().locator('table').first(), 2500);
      },
    },
    {
      say: 'Now we use the activity in a workflow: a W P S leg with the new check. We start a blank leg and paste its header: it fills the corporate workflow\'s scheme leg slot, and says what it reads and how it can end.',
      do: async () => {
        await d.go('designer');
        await d.select(p().getByLabel('Start from'), { label: 'Blank' });
        await d.select(p().getByLabel('Kind', { exact: true }), 'workflow');
        await d.select(p().getByLabel('Workflow kind'), 'leg');
        await d.type(p().getByLabel('Name', { exact: true }), D.PERIOD_LEG_HEADER.name, 25);
        await d.type(p().getByLabel('Title', { exact: true }), D.PERIOD_LEG_HEADER.title, 15);
        await d.click(p().getByRole('button', { name: 'Create' }));
        for (let i = 0; i < 80 && !(await p().getByLabel('Workflow YAML').inputValue()).includes(D.PERIOD_LEG_HEADER.name); i++) await p().waitForTimeout(100);
        await d.paste(p().getByLabel('Workflow YAML'), toYaml(D.PERIOD_LEG_HEADER));
        await d.wait(900);
      },
    },
    {
      say: 'On the canvas we insert steps from the palette. A call to the block that sets the business date. Then the W P S file checks, whose repair and reject outcomes end the leg as rejected.',
      do: async () => {
        await centre(main().locator('.cv-steps').first());
        await canvasCall(0, 'business-date', 'business-date', 'uae.p.enrich.business-date.v1', 0);
        await canvasCall(1, 'file-checks', 'salary-file', 'uae.wps.validate.salary-file.v1', 2);
      },
    },
    {
      say: 'Then our own new block, the pay period check. The canvas shows its reject outcome in red until we say what to do. It ends the leg as rejected. The rest of the leg is built the same way, so we paste the finished steps.',
      do: async () => {
        await canvasCall(2, 'pay-period', 'pay-period', D.PERIOD_ACTIVITY.name, 1);
        const y: string = await p().getByLabel('Workflow YAML').inputValue();
        for (const want of ['id: business-date', 'id: file-checks', 'id: pay-period', `call: ${D.PERIOD_ACTIVITY.name}`]) if (!y.includes(want)) throw new Error(`The canvas did not write ${want}:\n${y}`);
        await d.paste(p().getByLabel('Workflow YAML'), toYaml(D.PERIOD_LEG));
        await d.wait(1200);
      },
    },
    {
      say: 'The checks say there is no error: every outcome is handled, and every block exists and is published. We save the leg and the checker publishes it.',
      do: async () => {
        await expectText(/no errors/);
        await save();
        await publish();
      },
    },
    {
      say: 'Now the leg on the bench, with simulators for the W P S system and the bank calendar, and a sample salary file of two lines, each paid for October.',
      do: async () => {
        await bench(D.PERIOD_LEG.name, D.WPS_PORTS);
        await withSample('samples/wps-sif-two-lines.sif');
      },
    },
    {
      say: 'Run. The business date, the file checks, our pay period check passes, each line is checked, the file is rendered and submitted, and the leg settles.',
      do: async () => { await run(/pay-period:pass/); },
    },
    {
      say: 'Now a file with a mistake. We add a sample file on the bench: the same file, but its second line pays thirty days. Samples are synthetic, never real names or accounts.',
      do: async () => {
        await centre(main().getByText('Input'));
        await d.click(p().getByRole('tab', { name: 'Inbound sample' }));
        await d.click(p().getByRole('button', { name: 'Add a sample file' }));
        await d.type(p().getByLabel('Sample file name'), D.BAD_PERIOD_SAMPLE_NAME, 30);
        await d.paste(p().getByLabel('Sample content'), D.BAD_PERIOD_SAMPLE);
        await d.click(p().getByRole('button', { name: /^Save the sample as / }));
        await d.wait(1200);
        await d.select(p().getByLabel('Inbound sample'), `samples/${D.BAD_PERIOD_SAMPLE_NAME}`);
      },
    },
    {
      say: 'Run. The file checks pass, but our pay period check rejects the file, and the leg ends as rejected. Nothing was sent to the scheme.',
      do: async () => { await run(/pay-period:reject/); },
    },

    // ------------------------------------------------------------------ 8. the debugger
    {
      card: ['8. The debugger', 'Why the leg ended as it did, one step at a time'],
      say: 'The debugger shows how a run got there. Instead of run, we choose debug step by step. On the left, the steps of the leg. On the right, what just happened, what it decided, what data changed, and the whole context.',
      do: async () => {
        await centre(main().getByText('Input'));
        await d.click(p().getByRole('button', { name: 'Debug step by step' }));
        await dbgBar().waitFor();
        await centre(dbgBar());
        await d.point(main().getByLabel('Workflow steps'), 2000);
        await d.point(p().getByTestId('dbg-now'), 1500);
      },
    },
    {
      say: 'Step into reads the file through the intake map, then enters the leg, and its steps appear. We set a breakpoint on the pay period step, and continue. The run stops there. Step into, and the script has run: its outcome is reject.',
      do: async () => {
        const bp = main().getByRole('button', { name: 'Breakpoint at pay-period' });
        for (let i = 0; i < 8 && !(await bp.count()); i++) { await d.click(dbgBar().getByRole('button', { name: 'Step into ▶' })); await d.wait(700); }
        await d.click(bp);
        await d.click(dbgBar().getByRole('button', { name: 'Continue ⏭' }));
        await d.wait(800);
        for (let i = 0; i < 4 && !/pay-period ran/.test((await p().getByTestId('dbg-now').textContent()) ?? ''); i++) { await d.click(dbgBar().getByRole('button', { name: 'Step into ▶' })); await d.wait(700); }
        await expectDbg(/pay-period ran/);
        await d.point(p().getByTestId('dbg-now'), 2000);
      },
    },
    {
      say: 'What changed: the findings our script wrote, with the line, the field, and the reason code. Step back moves the run back one event, exactly, because every run is on the studio\'s own clock. Continue runs to the end.',
      do: async () => {
        await d.point(p().getByTestId('dbg-changed'), 3000);
        await d.click(dbgBar().getByRole('button', { name: 'Step back' }));
        await d.wait(900);
        await d.click(dbgBar().getByRole('button', { name: 'Continue ⏭' }));
        await d.wait(900);
      },
    },

    // ------------------------------------------------------------------ 9. the explain panel and the assistant
    {
      card: ['9. The explain panel and the assistant', 'Beside every editor'],
      say: 'Beside every editor sits the assistant. We open our activity again and open the panel. Its first tab, what is happening, needs no model. Click into a field, and it says what the field means, the values it takes, an example and the usual mistake. Here, the effect.',
      do: async () => {
        await d.go(`editors?kind=activity&open=${D.PERIOD_ACTIVITY.name}`);
        await expectText(/pay-period/);
        await d.click(p().getByRole('button', { name: 'Open the assistant' }));
        await expectCount(dock(), 1);
        await p().getByLabel('Effect').focus();
        for (let i = 0; i < 80 && !(await dock().getByTestId('field-help').count()); i++) await p().waitForTimeout(250);
        await d.point(dock().getByTestId('field-help'), 3500);
      },
    },
    {
      say: 'Below it, the document explained in plain words, a checklist of what is left before it can be published, and the next step. It follows every edit.',
      do: async () => { await d.point(dock().getByTestId('explanation'), 4000); },
    },
    ...(AI_LIVE ? [] : [{
      say: 'The second tab, ask, is Claude with tools: it reads, runs the bench, and proposes changes that you allow once, allow always, or deny. It never publishes. It needs a model key on the studio server. In this recording there is none, so it says so plainly.',
      do: async () => {
        await d.click(dock().getByRole('tab', { name: 'Ask' }));
        await d.point(dock().locator('.alert').first(), 3000);
        await d.click(dock().getByRole('button', { name: 'Close the assistant' }));
      },
    }]),
    {
      say: 'The A I assistant page asks for a whole draft from a template: a contract, a rule, a script, a simulator or a map, from a scheme\'s text and samples. A draft comes back with tests and every gate, or shows the gate that is red. Nothing is saved until you save it.',
      do: async () => {
        await d.go('assistant');
        await d.wait(1200);
        await d.point(main().locator('.panel').nth(1), 3500);
      },
    },

    // ------------------------------------------------------------------ 10. import
    {
      card: ['10. Import', 'A scheme brought in from its source code'],
      say: 'Import brings a scheme in from the source code of an older system. An A I coding agent reads the source with the studio\'s extraction prompt and writes a bundle. We use the training bundle, and preview it.',
      do: async () => {
        await d.go('import');
        await d.click(p().getByRole('button', { name: 'Use the training bundle' }));
        await d.click(p().getByRole('button', { name: 'Preview' }));
        await expectText(/may be imported|error/, 120);
      },
    },
    {
      say: 'The preview says whether it may be imported: its coverage of the source, its findings, its own tests run against the bundle, and its checks. Imported, everything is a draft, and nothing is a version until a checker approves it.',
      do: async () => {
        await d.point(panel(/^2\. The preview/), 3000);
        await centre(panel(/^Coverage of the source/));
        await d.point(panel(/^Coverage of the source/), 2500);
      },
    },

    // ------------------------------------------------------------------ 11. export to the four engines
    {
      card: ['11. Export to the four engines', 'Temporal, DTFx, Flowable and Elsa'],
      say: 'A workflow is independent of the engine that runs it. The export builds a package for one of four engines: Temporal, D T F X, Flowable or Elsa. Here, the corporate workflow for Temporal.',
      do: async () => {
        await d.go('export?workflows=p.wf.corporate.v1&target=temporal');
        await d.click(p().getByRole('button', { name: 'Preview' }));
        await expectText(/promotable/, 60);
        await d.point(panel(/^Package/), 3000);
      },
    },
    {
      say: 'The same workflow for Flowable, and for Elsa, and for D T F X. Each package holds the expanded workflow, a worker per task queue, and its manifest. The portability notes say what each engine does differently.',
      do: async () => {
        for (const t of ['flowable', 'elsa', 'dtfx']) {
          await d.click(p().getByRole('radio', { name: t }));
          await d.click(p().getByRole('button', { name: 'Preview' }));
          await d.wait(1500);
        }
        await d.click(p().getByRole('tab', { name: 'Tasks' }).or(p().getByRole('tab', { name: 'Portability notes' })).first());
        await d.wait(1500);
      },
    },
    {
      say: 'The conformance kit proves an engine runs the language the same way. Its scenarios run here on the studio\'s interpreter, and the same scenarios run on each of the four engines from the command line. Run all.',
      do: async () => {
        await d.go('kit');
        await d.click(p().getByRole('button', { name: /^Run all/ }));
        for (let i = 0; i < 600 && (await p().getByRole('button', { name: 'Running…' }).count()); i++) await p().waitForTimeout(250);
        await d.point(panel(/^Scenarios/), 3500);
      },
    },

    // ------------------------------------------------------------------ 12. governance and publishing
    {
      card: ['12. Governance and publishing', 'Who may do what, and what is published'],
      say: 'Governance. Users and roles: who may do what, in which scopes. The server checks every write, request and approval against this table.',
      do: async () => {
        await d.go('governance?tab=users');
        await d.wait(1200);
        await d.point(main().locator('.panel').first(), 3000);
      },
    },
    {
      say: 'Packs: each scope released as a pack with its release gate, every test green and every check passed. Pins and retirement: which releases banks still use. And the audit log, a chain of every save and approval, which the studio can verify.',
      do: async () => {
        await d.go('governance?tab=packs');
        await d.wait(1500);
        await d.go('governance?tab=releases');
        await d.wait(1500);
        await d.go('governance?tab=audit');
        await d.click(p().getByRole('button', { name: 'Verify the chain' }));
        await expectText(/The chain verifies/);
        await d.point(main().getByText('The chain verifies').first(), 2000);
      },
    },
    {
      say: 'The studio\'s only store is a GitHub branch. Each change set in progress is a branch of its own, and an approval is one commit on the content branch, or a pull request. A published file is never rewritten. When a bank approver approves a bank\'s change, the studio builds and signs the bank\'s distribution.',
      do: async () => {
        await d.go('export');
        await centre(main().getByText('Change sets in progress'));
        await d.wait(2500);
      },
    },
    {
      card: ['The designer designs. The developer scripts.', 'Every change checked, tested, and approved by another person'],
      say: 'That is the studio for the designer and the developer. The catalogue and the data. A rule and its examples. Workflows on the canvas, schemes, ports and simulators. A new activity written with a script, tested, used in a leg, run on the bench and in the debugger. Then the assistant, import, the export to four engines, the kit, and governance.',
      do: async () => { await d.wait(1500); await d.go('catalogue'); },
    },
  ];
}
