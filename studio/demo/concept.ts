import * as path from 'node:path';
import { pathToFileURL } from 'node:url';
import { SLOW, type Director, type Scene } from './record.js';
import { STUDIO } from '../test/blackbox/server.js';

// Video 1, the concept: every idea of TransactHub drawn as a kitchen (demo/explainer/ideas.html), then a short tour of the
// same ideas in the studio on Fluent UI 2. It replaces the earlier ideas and concepts videos. Plain words, a slow pace.

export const NAME = '1-concept';
export const TITLE = 'TransactHub: the concept, explained with a kitchen, then shown in the studio';
export const PACE = SLOW;

const PAGE = pathToFileURL(path.join(STUDIO, 'demo', 'explainer', 'ideas.html')).href;
export async function open(d: Director) {
  await d.page.goto(PAGE);
}

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');
  const panel = (title: RegExp) => main().locator('.panel', { has: p().locator('h3', { hasText: title }) }).first();
  const K = (js: string) => p().evaluate(js);
  const settle = () => d.wait(900);
  const j = JSON.stringify;
  const card = (id: string, o: Record<string, unknown>) => K(`K.card(${j(id)}, ${j(o)})`);
  const arrow = (id: string, a: string, b: string, o: Record<string, unknown> = {}) => K(`K.arrow(${j(id)}, ${j(a)}, ${j(b)}, ${j(o)})`);
  const note = (id: string, text: string, o: Record<string, unknown>) => K(`K.note(${j(id)}, ${j(text)}, ${j(o)})`);
  const pill = (id: string, text: string, o: Record<string, unknown>) => K(`K.pill(${j(id)}, ${j(text)}, ${j(o)})`);
  const chapter = (n: string, t: string, s = '') => K(`K.chapter(${j(n)}, ${j(t)}, ${j(s)})`);
  const travel = (ids: string[], ms = 1300) => K(`K.travel(${j(ids)}, ${ms})`);
  const light = (...ids: string[]) => K(`K.light(...${j(ids)})`);
  const unlight = (...ids: string[]) => K(`K.unlight(...${j(ids)})`);

  return [
    // ------------------------------------------------------------------ the explainer
    {
      card: ['TransactHub: the concept', 'The ideas, explained with a kitchen. Then the same ideas in the studio.'],
      say: 'Welcome. This video explains the ideas behind TransactHub. You do not need to know anything about payments. First, we draw every idea as a kitchen. Then we find the same ideas in the studio.',
    },
    // 1. Two kitchens
    {
      say: 'Think of a payment as an order in a restaurant. Before a restaurant opens, its recipes are written and tried out in a test kitchen. The studio is that test kitchen. The studio designs.',
      do: async () => {
        await chapter('1', 'Two kitchens', 'The studio designs, the runtime runs');
        await card('test', { x: 90, y: 230, w: 360, icon: 'blueprint', k: 'The test kitchen', r: 'The studio', d: 'Recipes are written and tried here.', c: 'recipe' });
      },
    },
    {
      say: 'The real kitchen cooks every order during service. In TransactHub, the real kitchen is called the runtime. The runtime runs. The studio never cooks a real order, and the runtime never writes a recipe.',
      do: async () => {
        await card('real', { x: 830, y: 230, w: 360, icon: 'kitchen', k: 'The restaurant kitchen', r: 'The runtime', d: 'Cooks every real order.', c: 'run' });
        await settle();
        await arrow('a1', 'test', 'real', { label: 'approved recipes go to service' });
      },
    },
    {
      say: 'Why keep them apart? Because a mistake in the test kitchen must never reach a real customer. A recipe goes to service only when it is finished, tested, and approved.',
      do: async () => { await note('why', 'A recipe reaches service only when it is <b>finished</b>, <b>tested</b> and <b>approved</b>.', { x: 330, y: 440, w: 620 }); },
    },
    // 2. One order
    {
      say: 'Let us follow one order. It is a file from a company, asking to pay its staff their salaries. First, the order is read and checked. Then the money is set aside, so it cannot be spent twice.',
      do: async () => {
        await chapter('2', 'One order\'s journey', 'A salary payment, from start to finish');
        await card('o', { x: 40, y: 270, w: 180, icon: 'ticket', k: 'The order arrives', r: 'A file', c: 'ticket' });
        await card('c', { x: 250, y: 270, w: 180, icon: 'check', k: 'Read and checked', r: 'Validate', c: 'station' });
        await card('s', { x: 460, y: 270, w: 180, icon: 'pot', k: 'Money set aside', r: 'Reserve', c: 'station' });
        await settle();
        await arrow('o-c', 'o', 'c');
        await arrow('c-s', 'c', 's');
      },
    },
    {
      say: 'Then the order goes out to a partner, the payment network, and we wait for its answer. When the answer comes back, the payment is written into the books. Watch the order travel.',
      do: async () => {
        await card('pp', { x: 670, y: 270, w: 180, icon: 'door', k: 'Sent to a partner', r: 'Exchange', c: 'door' });
        await card('a', { x: 880, y: 270, w: 180, icon: 'talk', k: 'The answer returns', r: 'Reply', c: 'door' });
        await card('b', { x: 1090, y: 270, w: 160, icon: 'log', k: 'In the books', r: 'Post', c: 'station' });
        await settle();
        await arrow('s-p', 's', 'pp');
        await arrow('p-a', 'pp', 'a');
        await arrow('a-b', 'a', 'b');
        await travel(['o-c', 'c-s', 's-p', 'p-a', 'a-b'], 1100);
      },
    },
    // 3. Stations and checks
    {
      say: 'A kitchen has stations, and each station does one job. In TransactHub, a station is called an activity. An activity says what it reads from the order, what it writes onto it, and how its job can end.',
      do: async () => {
        await chapter('3', 'Stations and checks', 'Activities and rules');
        await card('rd', { x: 60, y: 150, w: 290, icon: 'ticket', k: 'What it reads', r: 'Reads', d: 'For example, the amount.', c: 'safety' });
        await card('st', { x: 480, y: 150, w: 320, icon: 'pot', k: 'A station', r: 'Activity', d: 'Does one job.', c: 'station' });
        await card('wr', { x: 930, y: 150, w: 290, icon: 'log', k: 'What it writes', r: 'Writes', d: 'For example, the result.', c: 'safety' });
        await settle();
        await arrow('rd-st', 'rd', 'st');
        await arrow('st-wr', 'st', 'wr');
      },
    },
    {
      say: 'The ways a job can end are called outcomes, such as pass or reject. Every outcome must lead somewhere, so an order is never left hanging.',
      do: async () => {
        await pill('pass', 'outcome: pass', { x: 480, y: 330, c: 'station' });
        await pill('rej', 'outcome: reject', { x: 660, y: 330, c: 'scheme' });
      },
    },
    {
      say: 'How does a checking station know what to check? It follows rules. A rule is one simple question, such as: is the amount in dirhams? If the answer is no, the rule writes a finding, with a short reason code.',
      do: async () => {
        await card('rule', { x: 60, y: 420, w: 340, icon: 'check', k: 'One question', r: 'Rule', d: 'Is the amount in dirhams?', c: 'rule' });
        await note('f', 'If not: a finding, with reason code <b>AM03</b>', { x: 440, y: 440, w: 380 });
      },
    },
    {
      say: 'Rules are grouped into a checklist, called a rule set. And every rule comes with worked examples, one that passes and one that fails, so a broken rule is caught at once.',
      do: async () => {
        await card('set', { x: 880, y: 420, w: 340, icon: 'recipe', k: 'A checklist', r: 'Rule set', d: 'With worked examples.', c: 'rule' });
        await settle();
        await arrow('r-s', 'rule', 'set', { bend: 60 });
      },
    },
    // 4. Values
    {
      say: 'Some numbers are not fixed in the recipe. They are dials, such as the largest amount allowed. A dial has a safe range and a default. Each restaurant may turn it, but only within that range. In TransactHub, the dials and settings a bank may change are called values.',
      do: async () => {
        await chapter('4', 'Dials', 'Values: parameters with a default and bounds');
        await card('dial', { x: 90, y: 160, w: 380, icon: 'dial', k: 'A dial', r: 'Parameter', d: 'Largest amount: default 1,000,000. Range 1 to 1,000,000.', c: 'bank' });
        await card('vals', { x: 560, y: 160, w: 630, icon: 'books', k: 'A bank\'s values', r: 'Values', d: 'Dials, rule values, which door leads where, its calendar of working days.', c: 'bank' });
      },
    },
    {
      say: 'Here is the important part. Values can change without touching a recipe. The recipe itself, the logic, stays exactly as it was approved.',
      do: async () => {
        await card('logic', { x: 90, y: 380, w: 520, icon: 'lock', k: 'The recipe stays fixed', r: 'Logic', d: 'Changed only by a new, approved edition.', c: 'recipe' });
        await card('turn', { x: 670, y: 380, w: 520, icon: 'dial', k: 'The dials may turn', r: 'Values', d: 'Within their range, from a chosen date.', c: 'bank' });
      },
    },
    // 5. Recipes, partners, slots and legs
    {
      say: 'Stations do nothing on their own. A recipe puts them in order: first this, then that, and for each outcome, what happens next. In TransactHub, a recipe is called a workflow.',
      do: async () => {
        await chapter('5', 'Recipes and partners', 'Workflows, schemes, slots and legs');
        await card('rc', { x: 60, y: 130, w: 300, icon: 'recipe', k: 'A recipe', r: 'Workflow', c: 'recipe' });
        await card('r1', { x: 60, y: 290, w: 250, icon: 'check', k: 'Check', r: 'step', c: 'station' });
        await card('r2', { x: 350, y: 290, w: 250, icon: 'pot', k: 'Set money aside', r: 'step', c: 'station' });
        await card('gap', { x: 640, y: 290, w: 270, icon: 'gap', k: 'The partner\'s part', r: 'Slot', c: 'safety' });
        await card('r4', { x: 950, y: 290, w: 250, icon: 'log', k: 'Book', r: 'step', c: 'station' });
        await settle();
        await arrow('r12', 'r1', 'r2');
        await arrow('r2g', 'r2', 'gap');
        await arrow('rg4', 'gap', 'r4');
      },
    },
    {
      say: 'Payments do not only come from the dining room. They come through partners, each with its own rules, such as the salary payment system of the United Arab Emirates. In payments, such a partner is called a scheme.',
      do: async () => {
        await card('sch', { x: 420, y: 130, w: 360, icon: 'network', k: 'A partner', r: 'Scheme', d: 'For example, the wages protection system, WPS.', c: 'scheme' });
      },
    },
    {
      say: 'The house recipe has a gap in the middle that says: now do the partner\'s part. That gap is a slot. Each scheme brings the piece that fills it. That piece is called a leg.',
      do: async () => {
        await card('leg', { x: 640, y: 470, w: 270, icon: 'piece', k: 'The partner\'s piece', r: 'Leg', c: 'recipe' });
        await settle();
        await arrow('leg-gap', 'leg', 'gap', { label: 'fills' });
      },
    },
    {
      say: 'So one house recipe works for every partner. Only the piece in the gap changes. A new scheme means a new leg, not a new kitchen.',
      do: async () => { await travel(['r12', 'r2g', 'rg4'], 1200); },
    },
    // 6. The factory
    {
      say: 'Where do the first recipes come from? The studio ships with a cookbook, ready on day one. It is called the factory.',
      do: async () => {
        await chapter('6', 'The cookbook that ships', 'The factory');
        await card('fac', { x: 440, y: 130, w: 400, icon: 'books', k: 'The shared cookbook', r: 'Factory', d: 'Ready on day one, read-only for banks.', c: 'recipe' });
      },
    },
    {
      say: 'The factory has three parts. The platform, with the house recipes every payment follows. The platform for the United Arab Emirates, with what every scheme there shares. And one scheme, WPS, with its leg. WPS is still provisional: it was written from public sources and waits to be confirmed.',
      do: async () => {
        await card('fp', { x: 60, y: 340, w: 360, icon: 'kitchen', k: 'The platform', r: 'p', d: 'House recipes for every payment.', c: 'recipe' });
        await card('fu', { x: 460, y: 340, w: 360, icon: 'globe', k: 'The UAE platform', r: 'uae.p', d: 'What every UAE scheme shares.', c: 'recipe' });
        await card('fw', { x: 860, y: 340, w: 360, icon: 'network', k: 'The salary scheme', r: 'uae.wps', d: 'Its leg and its formats. Provisional.', c: 'scheme' });
        await settle();
        for (const x of ['fp', 'fu', 'fw']) await arrow(`fac-${x}`, 'fac', x);
      },
    },
    // 7. A bank's own
    {
      say: 'Many banks share the cookbook, like restaurants in a franchise. Each bank has its own space that only its own people can see. A bank cannot rewrite the shared recipes. It sets its own values instead.',
      do: async () => {
        await chapter('7', 'A bank\'s own', 'Hooks, values, and two people for every change');
        await card('cb', { x: 470, y: 120, w: 340, icon: 'books', k: 'The shared cookbook', r: 'Factory', c: 'recipe' });
        for (const [i, n] of ['Bank A', 'Bank B', 'Bank C'].entries()) await card(`b${i}`, { x: 120 + i * 370, y: 280, w: 300, icon: 'store', k: n, r: 'its own space', c: 'bank' });
        await settle();
        for (const i of [0, 1, 2]) await arrow(`cb-b${i}`, 'cb', `b${i}`);
      },
    },
    {
      say: 'The house recipe also has marked spots where a bank may add a step of its own, like its house garnish before a dish goes out. These spots are hook points, and the bank\'s own step is a hook. A hook can hold a payment for a person to approve, add a detail, or let someone know.',
      do: async () => { await card('hk', { x: 120, y: 450, w: 400, icon: 'plug', k: 'Its own extra step', r: 'Hook', d: 'Added at a marked spot. Never edits the shared recipe.', c: 'bank' }); },
    },
    {
      say: 'And nobody changes anything alone. The person who makes a change never approves it. A second person checks it. This is called maker and checker, and every change is written in a log that cannot be quietly edited.',
      do: async () => { await card('mc', { x: 600, y: 450, w: 560, icon: 'people', k: 'Two people', r: 'Maker and checker', d: 'One makes the change, another approves it. All in the log.', c: 'safety' }); },
    },
    // 8. Going to service
    {
      say: 'Now, how do the approved recipes reach a bank\'s real kitchen? The studio packs a sealed box for that bank. It holds everything the bank runs: the platform, the schemes it has licensed, and its own hooks. The box is signed, so nobody can change it on the way. This box is called a distribution.',
      do: async () => {
        await chapter('8', 'Going to service', 'The distribution and the configuration release');
        await card('dist', { x: 60, y: 140, w: 560, icon: 'crate', k: 'A sealed box of recipes', r: 'Distribution', d: 'Platform, licensed schemes, the bank\'s own hooks. Signed.', c: 'recipe' });
      },
    },
    {
      say: 'The bank\'s values travel separately, on a signed card of their own. It says which dials are set to what, and from which date. This card is called a configuration release. It changes values only, never a recipe.',
      do: async () => {
        await card('cfg', { x: 660, y: 140, w: 560, icon: 'dial', k: 'A signed card of values', r: 'Configuration release', d: 'Dials, rule values, bindings, calendars. Dated.', c: 'bank' });
      },
    },
    {
      say: 'Why two? Because a bank changes a limit far more often than a recipe. A new value needs only a new card. A new recipe needs a new box. And a bank approver must agree before either one is put in force.',
      do: async () => {
        await card('kit', { x: 410, y: 380, w: 460, icon: 'kitchen', k: 'The bank\'s real kitchen', r: 'Runtime', d: 'Puts each in force on a bank approver\'s consent.', c: 'run' });
        await settle();
        await arrow('d-k', 'dist', 'kit', { label: 'new recipes', side: 'v' });
        await arrow('c-k', 'cfg', 'kit', { label: 'new values', side: 'v' });
      },
    },
    {
      say: 'An order already cooking keeps the box it started with, to the very end. A payment never mixes two editions of a recipe.',
      do: async () => { await note('pin', 'A payment is <b>pinned</b> to the distribution it started on.', { x: 290, y: 510, w: 700 }); },
    },
    // 9. One bank per runtime
    {
      say: 'One last idea. The test kitchen is shared: the platform\'s authors, the scheme authors and every bank work in one studio, each in their own space. But every bank has its own real kitchen. One bank per runtime.',
      do: async () => {
        await chapter('9', 'One bank per runtime', 'One shared studio, a runtime for each bank');
        await card('st1', { x: 460, y: 120, w: 360, icon: 'blueprint', k: 'One shared test kitchen', r: 'Studio', d: 'Every bank in its own space.', c: 'recipe' });
        for (const [i, n] of ['Bank A', 'Bank B', 'Bank C'].entries()) await card(`rt${i}`, { x: 120 + i * 370, y: 330, w: 300, icon: 'kitchen', k: `${n}\'s kitchen`, r: 'Runtime', c: 'run' });
        await settle();
        for (const i of [0, 1, 2]) await arrow(`st-rt${i}`, 'st1', `rt${i}`, { label: `box ${'ABC'[i]}` });
      },
    },
    {
      say: 'A runtime is told its bank when it is installed. It refuses another bank\'s box, values, files or backups, whoever asks. So one bank\'s payments can never end up in another bank\'s kitchen.',
      do: async () => {
        await note('one', 'A runtime takes <b>its own bank\'s</b> boxes and values, and refuses every other.', { x: 330, y: 500, w: 620 });
        await light('rt1');
      },
    },
    // 10. Together
    {
      say: 'Let us put it all together. The factory gives the house recipes and a scheme\'s leg. Activities do the work, following rules. A bank adds hooks and sets its values. Everything is tested and approved by two people.',
      do: async () => {
        await chapter('10', 'Everything together', '');
        await card('A', { x: 40, y: 130, w: 220, icon: 'books', k: 'Cookbook', r: 'Factory', c: 'recipe' });
        await card('B', { x: 300, y: 130, w: 220, icon: 'recipe', k: 'Recipes', r: 'Workflows', c: 'recipe' });
        await card('C', { x: 560, y: 130, w: 220, icon: 'piece', k: 'Partner\'s piece', r: 'Leg', c: 'scheme' });
        await card('D', { x: 820, y: 130, w: 200, icon: 'pot', k: 'Stations', r: 'Activities', c: 'station' });
        await card('E', { x: 1060, y: 130, w: 180, icon: 'check', k: 'Checks', r: 'Rules', c: 'rule' });
        await card('F', { x: 300, y: 300, w: 220, icon: 'plug', k: 'Bank steps', r: 'Hooks', c: 'bank' });
        await card('G', { x: 560, y: 300, w: 220, icon: 'dial', k: 'Bank dials', r: 'Values', c: 'bank' });
        await card('H', { x: 820, y: 300, w: 200, icon: 'people', k: 'Two people', r: 'Approval', c: 'safety' });
        await settle();
        await arrow('AB', 'A', 'B'); await arrow('BC', 'B', 'C'); await arrow('CD', 'C', 'D'); await arrow('DE', 'D', 'E');
        await arrow('BF', 'B', 'F'); await arrow('FG', 'F', 'G'); await arrow('GH', 'G', 'H');
      },
    },
    {
      say: 'Then the studio packs the bank\'s distribution and its configuration release, and the bank\'s own runtime cooks every order for real.',
      do: async () => {
        await card('I', { x: 300, y: 470, w: 260, icon: 'crate', k: 'Sealed box', r: 'Distribution', c: 'recipe' });
        await card('J', { x: 600, y: 470, w: 280, icon: 'dial', k: 'Card of values', r: 'Configuration release', c: 'bank' });
        await card('L', { x: 940, y: 470, w: 260, icon: 'kitchen', k: 'The bank\'s kitchen', r: 'Runtime', c: 'run' });
        await settle();
        await arrow('HI', 'H', 'I'); await arrow('IJ', 'I', 'J'); await arrow('JL', 'J', 'L');
        await travel(['AB', 'BC', 'CD', 'DE'], 900);
        await unlight('E');
        await travel(['BF', 'FG', 'GH', 'HI', 'IJ', 'JL'], 900);
        await unlight('L');
      },
    },
    // ------------------------------------------------------------------ the tour
    {
      card: ['Now in the studio', 'The same ideas, on the screen.'],
      say: 'Now let us find the same ideas in the studio itself.',
    },
    {
      say: 'This is the studio. Across the top is the navy bar, with search, the tenant we work in, and who is signed in. On the left are the studio\'s surfaces. The catalogue lists everything in the cookbook.',
      do: async () => {
        await d.go('catalogue');
        await d.point(p().locator('header').first(), 2500);
        await d.point(p().locator('nav').first(), 2000);
      },
    },
    {
      say: 'The factory\'s three parts are here as scopes. Let us pick the salary scheme, WPS. Every item has a name with four parts: the scope, the type, the name, and the major version.',
      do: async () => {
        await d.select(main().getByLabel('Scope'), 'uae.wps');
        await d.point(main().locator('table').first(), 2500);
      },
    },
    {
      say: 'Here is an activity, a station: it checks an account number, called an IBAN. Its page says what it reads, what it writes, and the outcomes it can end with.',
      do: async () => {
        await d.select(main().getByLabel('Scope'), '');
        await d.type(main().getByRole('searchbox', { name: 'Search' }), 'iban', 120);
        await d.click(p().locator('a[href="#/artefact/uae.p.validate.iban.v1"]').first());
        await d.scroll(450);
      },
    },
    {
      say: 'A rule opens in its own editor, as a form. Below it are its worked examples, and the checks that run as you edit.',
      do: async () => {
        await d.go('editors?kind=rule');
        await d.scroll(500);
        await d.scroll(500);
      },
    },
    {
      say: 'This is a workflow, the corporate payment workflow, the house recipe. On the left is its text, on the right the same recipe drawn as steps. Its slot, the scheme leg, waits for a scheme to fill it, and its hook points wait for a bank.',
      do: async () => {
        await d.go('designer');
        await d.select(p().getByLabel('Published workflow'), { label: 'p.wf.corporate.v1 @1.0.0' });
        await d.click(p().getByRole('button', { name: 'Open', exact: true }).last());
        await p().waitForTimeout(800);
        await d.point(main().locator('.cv-steps').first(), 3500);
      },
    },
    {
      say: 'Here is the scheme\'s order desk, its dispatch table. For WPS, a salary file starts the corporate payment workflow, and the WPS leg fills its slot.',
      do: async () => {
        await d.go('editors?kind=scheme&open=' + encodeURIComponent('scheme:uae.wps'));
        const table = panel(/^Scheme and dispatch table/);
        await d.point(table.locator('tr', { hasText: 'uae.wps.sif' }).first(), 4000);
        await d.point(table.getByText('Legs:'), 2500);
      },
    },
    {
      say: 'On the test bench, a test runs a recipe with a stand-in for the partner, called a simulator. A wait of days ends at once. Each step of the run is listed, with how it ended.',
      do: async () => {
        await d.go('tests');
        await d.type(p().getByLabel('Filter tests'), 'WPS-OUT-001', 60);
        await d.click(p().locator('tr', { hasText: 'WPS-OUT-001' }).first().getByRole('button', { name: 'Run' }));
        await p().waitForTimeout(1500);
        await d.scroll(500);
      },
    },
    {
      say: 'Now we sign in as a bank\'s configurator. The bank workspace gathers what is the bank\'s own: its hooks, its rules and their values, its dials, and the schemes it has licensed.',
      do: async () => {
        await d.go('bank');
        await d.point(panel(/^Hooks/), 1800);
        await d.point(panel(/^Schemes licensed/), 1800);
        await d.point(panel(/^Dials/), 1800);
      },
    },
    {
      say: 'The bank\'s values live in its configuration set, with a release number. Every dial is checked against the range the owner allowed. A value outside it is refused.',
      do: async () => {
        await d.go('tenant');
        await d.point(panel(/^Configuration set/), 2200);
        await d.scroll(450);
      },
    },
    {
      say: 'Governance keeps the record. Every save, approval and refusal is in the audit log, and the studio can prove that nobody has changed it.',
      do: async () => {
        await d.go('governance?tab=audit');
        await d.click(p().getByRole('button', { name: 'Verify the chain' }));
        await p().waitForTimeout(800);
      },
    },
    {
      say: 'Last, the export: the package a bank\'s runtime runs. When a bank approver approves the bank\'s change, the studio builds and signs the bank\'s distribution, and its runtime installs it.',
      do: async () => {
        await d.go('export?workflows=p.wf.corporate.v1&target=dtfx');
        await d.select(p().getByLabel('Export tenant'), 'example-bank');
        await d.click(p().getByRole('button', { name: 'Preview' }));
        await p().waitForTimeout(1200);
        await d.point(panel(/^Package/), 3500);
      },
    },
    {
      card: ['The studio designs. The runtime runs.', 'Next: the installation, the north star, a payment end to end, then the bank configurator, the studio designer and developer, and the service console.'],
      say: 'That is the concept. The studio designs, tests and approves. Each bank\'s own runtime runs. The next videos install it, explain the north star, and follow a payment end to end. Then one video for each console: the bank configurator, the studio designer and developer, and the service console.',
    },
  ];
}
