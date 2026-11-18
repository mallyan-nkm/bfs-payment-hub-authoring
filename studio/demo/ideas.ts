import * as path from 'node:path';
import { pathToFileURL } from 'node:url';
import { SLOW, type Director, type Scene } from './record.js';
import { STUDIO } from '../test/blackbox/server.js';

// Training video 1: every idea of the studio, explained with a kitchen, without showing the app.
// The pictures are drawn by demo/explainer/ideas.html; each line of the voice adds the next piece.

export const NAME = 'training-1-the-ideas';
export const TITLE = 'TransactHub Designer Studio training, part 1: the ideas, explained with a kitchen';
export const PACE = SLOW;

const PAGE = pathToFileURL(path.join(STUDIO, 'demo', 'explainer', 'ideas.html')).href;
export async function open(d: Director) {
  await d.page.goto(PAGE);
}

export function scenes(d: Director): Scene[] {
  const K = (js: string) => d.page.evaluate(js);
  const settle = () => d.wait(900);
  const card = (id: string, o: Record<string, unknown>) => K(`K.card(${JSON.stringify(id)}, ${JSON.stringify(o)})`);
  const arrow = (id: string, a: string, b: string, o: Record<string, unknown> = {}) => K(`K.arrow(${JSON.stringify(id)}, ${JSON.stringify(a)}, ${JSON.stringify(b)}, ${JSON.stringify(o)})`);
  const note = (id: string, text: string, o: Record<string, unknown>) => K(`K.note(${JSON.stringify(id)}, ${JSON.stringify(text)}, ${JSON.stringify(o)})`);
  const pill = (id: string, text: string, o: Record<string, unknown>) => K(`K.pill(${JSON.stringify(id)}, ${JSON.stringify(text)}, ${JSON.stringify(o)})`);
  const chapter = (n: string, t: string, s = '') => K(`K.chapter(${JSON.stringify(n)}, ${JSON.stringify(t)}, ${JSON.stringify(s)})`);
  const travel = (ids: string[], ms = 1400) => K(`K.travel(${JSON.stringify(ids)}, ${ms})`);
  const light = (...ids: string[]) => K(`K.light(...${JSON.stringify(ids)})`);
  const unlight = (...ids: string[]) => K(`K.unlight(...${JSON.stringify(ids)})`);

  return [
    // ------------------------------------------------------------------ 1
    {
      card: ['Part 1: the ideas', 'Every building block of the TransactHub Designer Studio, explained with a kitchen. No screens yet.'],
      say: 'Welcome. This video explains the ideas behind the TransactHub Designer Studio, without showing any screens. You do not need to know anything about banking or payments. We will use one picture all the way through: a kitchen.',
    },
    {
      say: 'Think of a payment as an order in a restaurant. Before a restaurant opens, its recipes are written down and tried out in a test kitchen. The studio is that test kitchen.',
      do: async () => {
        await chapter('1', 'What the studio is', 'The test kitchen and the restaurant');
        await card('test', { x: 110, y: 250, w: 340, icon: 'blueprint', k: 'The test kitchen', r: 'The studio', d: 'Recipes are written and tried here.', c: 'recipe' });
      },
    },
    {
      say: 'When the recipes are ready, they are sent to the restaurant\'s real kitchen, which cooks every order during service. In payments, that real kitchen is called the engine. The studio never cooks a real order itself.',
      do: async () => {
        await card('real', { x: 830, y: 250, w: 340, icon: 'kitchen', k: 'The restaurant kitchen', r: 'The engine', d: 'Cooks every real order.', c: 'run' });
        await settle();
        await arrow('a1', 'test', 'real', { label: 'recipes go to service' });
      },
    },
    // ------------------------------------------------------------------ 2
    {
      say: 'Let us follow one order through the kitchen. Our order is a file from a company, asking to pay its staff their salaries.',
      do: async () => {
        await chapter('2', 'One order\'s journey', 'A payment, from start to finish');
        await card('o', { x: 40, y: 270, w: 180, icon: 'ticket', k: 'The order arrives', r: 'A file', c: 'ticket' });
      },
    },
    {
      say: 'First, the order is read and checked. Then the ingredients are set aside. For a payment, that means putting the money aside, so it cannot be spent twice.',
      do: async () => {
        await card('c', { x: 250, y: 270, w: 180, icon: 'check', k: 'Read and checked', r: 'Validate', c: 'station' });
        await card('s', { x: 460, y: 270, w: 180, icon: 'pot', k: 'Money set aside', r: 'Reserve', c: 'station' });
        await settle();
        await arrow('o-c', 'o', 'c');
        await arrow('c-s', 'c', 's');
      },
    },
    {
      say: 'Then the order goes out to a partner, the payment network, and we wait for its answer. When the answer comes back, the order is written into the books, and it is done.',
      do: async () => {
        await card('p', { x: 670, y: 270, w: 180, icon: 'door', k: 'Sent to a partner', r: 'Exchange', c: 'door' });
        await card('a', { x: 880, y: 270, w: 180, icon: 'talk', k: 'The answer returns', r: 'Reply', c: 'door' });
        await card('b', { x: 1090, y: 270, w: 160, icon: 'log', k: 'Written in the books', r: 'Post', c: 'station' });
        await settle();
        await arrow('s-p', 's', 'p');
        await arrow('p-a', 'p', 'a');
        await arrow('a-b', 'a', 'b');
      },
    },
    {
      say: 'Watch the order travel. Everything in the rest of this video is a closer look at one part of this journey.',
      do: async () => { await travel(['o-c', 'c-s', 's-p', 'p-a', 'a-b']); },
    },
    // ------------------------------------------------------------------ 3
    {
      say: 'A kitchen has stations, and each station does one job. One checks, one weighs, one cooks, one plates. In the studio, a station is called an activity.',
      do: async () => {
        await chapter('3', 'Stations', 'Activities');
        await card('st', { x: 470, y: 150, w: 340, icon: 'pot', k: 'A station', r: 'Activity', d: 'Does one job, and does it well.', c: 'station' });
      },
    },
    {
      say: 'Every activity says what it reads from the order, and what it writes onto it. A checking station might read the amount, and write down whether it passed.',
      do: async () => {
        await card('rd', { x: 80, y: 160, w: 300, icon: 'ticket', k: 'What it reads', r: 'Reads', d: 'For example, the amount.', c: 'safety' });
        await card('wr', { x: 900, y: 160, w: 300, icon: 'log', k: 'What it writes', r: 'Writes', d: 'For example, the check result.', c: 'safety' });
        await settle();
        await arrow('rd-st', 'rd', 'st');
        await arrow('st-wr', 'st', 'wr');
      },
    },
    {
      say: 'The ways a job can end are called outcomes. A checking station might end with pass, or with reject. Every outcome must lead somewhere in the recipe, so an order is never left hanging.',
      do: async () => {
        await pill('pass', 'outcome: pass', { x: 470, y: 380, c: 'station' });
        await pill('rej', 'outcome: reject', { x: 670, y: 380, c: 'scheme' });
        await settle();
        await arrow('st-pass', 'st', 'pass');
        await arrow('st-rej', 'st', 'rej');
      },
    },
    {
      say: 'There are a few kinds of station: checking, looking something up, translating, sending to a partner, holding for a person to approve, and writing into the books.',
      do: async () => {
        const kinds: [string, string][] = [['Check', 'validate'], ['Look up', 'enrich'], ['Translate', 'map'], ['Send', 'exchange'], ['Hold for approval', 'hold'], ['Book', 'post']];
        for (const [i, [k, r]] of kinds.entries()) await card(`k${i}`, { x: 70 + i * 195, y: 470, w: 180, icon: ['check', 'actor', 'talk', 'door', 'people', 'log'][i], k, r, c: 'station' });
      },
    },
    // ------------------------------------------------------------------ 4
    {
      say: 'How does a checking station know what to check? It follows quality checks. A quality check is one simple question, such as: is the amount in dirhams? In the studio, a quality check is called a rule.',
      do: async () => {
        await chapter('4', 'Quality checks', 'Rules, rule sets, decision tables and parameters');
        await card('rule', { x: 60, y: 140, w: 330, icon: 'check', k: 'A quality check', r: 'Rule', d: 'Is the amount in dirhams?', c: 'rule' });
      },
    },
    {
      say: 'If the answer is no, the rule writes a finding: what is wrong, and a short reason code that everyone in payments understands.',
      do: async () => { await note('f', 'Finding: <b>the currency is not dirhams</b>, reason code <b>AM03</b>', { x: 60, y: 300, w: 400 }); },
    },
    {
      say: 'Rules are grouped into a checklist, called a rule set. A checking station runs its checklist.',
      do: async () => {
        await card('set', { x: 490, y: 140, w: 300, icon: 'recipe', k: 'A checklist', r: 'Rule set', d: 'Several checks together.', c: 'rule' });
        await card('chk', { x: 900, y: 140, w: 320, icon: 'pot', k: 'A checking station', r: 'Activity', d: 'Runs the checklist.', c: 'station' });
        await settle();
        await arrow('r-s', 'rule', 'set');
        await arrow('s-c', 'set', 'chk');
      },
    },
    {
      say: 'Some choices are easier as a chart. For example, which delivery service to use for which kind of order. A chart like that is a decision table.',
      do: async () => { await card('tab', { x: 490, y: 330, w: 330, icon: 'chart', k: 'A chart', r: 'Decision table', d: 'Which partner for which order.', c: 'rule' }); },
    },
    {
      say: 'And some numbers are left as dials, such as the largest amount allowed. A dial has a safe range, and each restaurant can set its own value within that range. These dials are called parameters.',
      do: async () => { await card('dial', { x: 900, y: 330, w: 320, icon: 'dial', k: 'A dial', r: 'Parameter', d: 'Set within a safe range.', c: 'rule' }); },
    },
    {
      say: 'Every rule comes with worked examples: one that passes, one that fails, and one right at the edge. The studio runs them every time the rule changes, so a broken rule is caught at once.',
      do: async () => {
        await pill('e1', 'example: passes', { x: 60, y: 470, c: 'station' });
        await pill('e2', 'example: fails', { x: 230, y: 470, c: 'scheme' });
        await pill('e3', 'example: at the edge', { x: 380, y: 470, c: 'rule' });
      },
    },
    // ------------------------------------------------------------------ 5
    {
      say: 'Stations do nothing on their own. A recipe puts them in order: first this, then that, and for each outcome, what happens next. In the studio, a recipe is called a workflow.',
      do: async () => {
        await chapter('5', 'Recipes', 'Workflows');
        await card('rc', { x: 60, y: 130, w: 300, icon: 'recipe', k: 'A recipe', r: 'Workflow', c: 'recipe' });
        await card('w1', { x: 60, y: 300, w: 220, icon: 'check', k: 'Check the order', r: 'step', c: 'station' });
        await card('w2', { x: 360, y: 300, w: 220, icon: 'pot', k: 'Set the money aside', r: 'step', c: 'station' });
        await card('w3', { x: 660, y: 300, w: 220, icon: 'door', k: 'Send to the partner', r: 'step', c: 'door' });
        await pill('wend', 'end: done', { x: 960, y: 330, c: 'station' });
        await pill('wrej', 'end: sent back', { x: 120, y: 480, c: 'scheme' });
        await settle();
        await arrow('w12', 'w1', 'w2', { label: 'pass' });
        await arrow('w23', 'w2', 'w3', { label: 'reserved' });
        await arrow('w3e', 'w3', 'wend', { label: 'accepted' });
        await arrow('w1r', 'w1', 'wrej', { label: 'reject' });
      },
    },
    {
      say: 'A recipe can branch, repeat a step for every item on the order, run steps side by side, wait for a while, and always tidy up at the end, whatever happened.',
      do: async () => { await note('wn', 'Branch · repeat for each item · side by side · wait · always tidy up at the end', { x: 420, y: 470, w: 620 }); },
    },
    {
      say: 'There are a few kinds of recipe. The house recipe, which every payment follows, is the platform workflow. The part that depends on the partner is a leg. A reusable part, used in several recipes, is a segment. And a quick answer at the counter, with no cooking at all, is a handler.',
      do: async () => {
        await K('K.remove("w1","w2","w3","wend","wrej","w12","w23","w3e","w1r","wn")');
        await card('h', { x: 60, y: 300, w: 270, icon: 'kitchen', k: 'The house recipe', r: 'Platform workflow', c: 'recipe' });
        await card('l', { x: 360, y: 300, w: 270, icon: 'piece', k: 'The partner\'s part', r: 'Leg', c: 'recipe' });
        await card('sg', { x: 660, y: 300, w: 270, icon: 'recipe', k: 'A reusable part', r: 'Segment', c: 'recipe' });
        await card('hd', { x: 960, y: 300, w: 270, icon: 'talk', k: 'A quick answer', r: 'Handler', c: 'recipe' });
      },
    },
    // ------------------------------------------------------------------ 6
    {
      say: 'Some steps are really a small routine. Getting fresh stock means: place the order with the supplier, wait for the delivery, and check what arrived. The recipe shows all of that as one step.',
      do: async () => {
        await chapter('6', 'A step with a routine inside', 'Composites, subflows and the expanded form');
        await card('cp', { x: 440, y: 130, w: 400, icon: 'box', k: 'Get fresh stock', r: 'Composite activity', d: 'One step in the recipe.', c: 'station' });
      },
    },
    {
      say: 'Inside, that routine is its own small recipe, called a subflow: send, wait for the answer, and read it. You see one step. The kitchen follows the routine inside.',
      do: async () => {
        await card('s1', { x: 160, y: 340, w: 260, icon: 'door', k: 'Place the order', r: 'Send', c: 'door' });
        await card('s2', { x: 510, y: 340, w: 260, icon: 'watch', k: 'Wait for delivery', r: 'Await', c: 'door' });
        await card('s3', { x: 860, y: 340, w: 260, icon: 'check', k: 'Check what arrived', r: 'Read', c: 'door' });
        await settle();
        await arrow('cp-s1', 'cp', 's1', { label: 'inside' });
        await arrow('s12', 's1', 's2');
        await arrow('s23', 's2', 's3');
      },
    },
    {
      say: 'When the recipes go to the real kitchen, every such step is opened up into its routine. That opened-up version is called the expanded form. It is what the engine actually follows.',
      do: async () => { await note('ex', 'The <b>expanded form</b>: every composite step opened into its routine.', { x: 330, y: 500, w: 620 }); },
    },
    // ------------------------------------------------------------------ 7
    {
      say: 'A kitchen deals with people outside it: suppliers, the bank\'s own accounts, a delivery service. Each one is reached through its own door. In the studio, a door is called a port.',
      do: async () => {
        await chapter('7', 'Doors, stand-ins and translators', 'Ports, simulators and maps');
        await card('kit', { x: 60, y: 180, w: 290, icon: 'kitchen', k: 'Our kitchen', r: 'Workflow', c: 'recipe' });
        await card('dr', { x: 495, y: 180, w: 290, icon: 'door', k: 'A door', r: 'Port', c: 'door' });
        await card('sup', { x: 930, y: 180, w: 290, icon: 'network', k: 'The supplier', r: 'The outside party', c: 'scheme' });
        await settle();
        await arrow('k-d', 'kit', 'dr');
        await arrow('d-s', 'dr', 'sup');
      },
    },
    {
      say: 'When we practise, we do not call the real supplier. A stand-in actor plays the supplier, following a script: say yes, say no, say nothing, or answer late. That stand-in is a simulator.',
      do: async () => {
        await card('act', { x: 930, y: 380, w: 290, icon: 'actor', k: 'A stand-in actor', r: 'Simulator', d: 'Plays the supplier in rehearsals.', c: 'test' });
        await settle();
        await arrow('a-d', 'act', 'dr', { label: 'in tests' });
        for (const [i, s] of ['accept', 'reject', 'no reply', 'late reply'].entries()) await pill(`sc${i}`, s, { x: 500 + i * 105, y: 530, c: 'test' });
      },
    },
    {
      say: 'Suppliers speak their own language. A translator turns their delivery notes into our kitchen\'s language, and turns our orders into theirs. That translator is a map.',
      do: async () => {
        await card('mp', { x: 60, y: 380, w: 290, icon: 'talk', k: 'A translator', r: 'Map', d: 'Their language to ours, and back.', c: 'door' });
        await settle();
        await arrow('m-k', 'mp', 'kit');
      },
    },
    // ------------------------------------------------------------------ 8
    {
      say: 'Inside the kitchen, every order is written on the same kind of ticket, whatever it looked like when it arrived. For payments, that ticket follows an international standard called I S O twenty oh twenty two. In the studio, the ticket is called the canonical context.',
      do: async () => {
        await chapter('8', 'One kind of ticket', 'The canonical context');
        await card('i1', { x: 60, y: 140, w: 260, icon: 'ticket', k: 'A salary file', r: 'arrives', c: 'ticket' });
        await card('i2', { x: 60, y: 285, w: 260, icon: 'ticket', k: 'An instant transfer', r: 'arrives', c: 'ticket' });
        await card('i3', { x: 60, y: 430, w: 260, icon: 'ticket', k: 'A bank order', r: 'arrives', c: 'ticket' });
        await card('std', { x: 640, y: 270, w: 380, icon: 'ticket', k: 'The standard ticket', r: 'Canonical context', d: 'Based on ISO 20022.', c: 'recipe' });
        await settle();
        for (const i of ['i1', 'i2', 'i3']) await arrow(`${i}-s`, i, 'std', { label: 'map' });
      },
    },
    {
      say: 'Some details on the ticket are private, like a person\'s name. They are marked, like allergens on a menu, and they must never appear in logs, tests or examples.',
      do: async () => { await card('pd', { x: 640, y: 450, w: 380, icon: 'allergy', k: 'Private details, marked', r: 'Personal data', d: 'Never in logs, tests or examples.', c: 'scheme' }); },
    },
    // ------------------------------------------------------------------ 9
    {
      say: 'Orders do not only come from the dining room. They come through partners, each with its own rules: a salary payment system, an instant payment system. In payments, such a partner is called a scheme.',
      do: async () => {
        await chapter('9', 'Partners', 'Schemes, dispatch tables, slots and legs');
        await card('sch', { x: 60, y: 130, w: 300, icon: 'network', k: 'A partner', r: 'Scheme', d: 'For example, a salary payment system.', c: 'scheme' });
      },
    },
    {
      say: 'Every partner has an order desk. For each kind of message that arrives, the desk says what it is, which translator reads it, and which recipe to start. That desk is the scheme\'s dispatch table.',
      do: async () => {
        await card('dk', { x: 470, y: 130, w: 300, icon: 'desk', k: 'The order desk', r: 'Dispatch table', d: 'Says what each message starts.', c: 'scheme' });
        await card('hr', { x: 900, y: 130, w: 320, icon: 'recipe', k: 'Starts the house recipe', r: 'Platform workflow', c: 'recipe' });
        await settle();
        await arrow('sch-dk', 'sch', 'dk', { label: 'a message arrives' });
        await arrow('dk-hr', 'dk', 'hr');
      },
    },
    {
      say: 'The house recipe has a gap in the middle that says: now do the partner\'s part. That gap is called a slot.',
      do: async () => {
        await card('r1', { x: 60, y: 330, w: 250, icon: 'check', k: 'Check', r: 'step', c: 'station' });
        await card('r2', { x: 350, y: 330, w: 250, icon: 'pot', k: 'Set money aside', r: 'step', c: 'station' });
        await card('gap', { x: 640, y: 330, w: 270, icon: 'gap', k: 'The partner\'s part', r: 'Slot', c: 'safety' });
        await card('r4', { x: 950, y: 330, w: 250, icon: 'log', k: 'Book', r: 'step', c: 'station' });
        await settle();
        await arrow('r12', 'r1', 'r2');
        await arrow('r2g', 'r2', 'gap');
        await arrow('rg4', 'gap', 'r4');
      },
    },
    {
      say: 'Each partner provides the piece that fills the gap. That piece is its leg. So one house recipe works for every partner: only the piece in the gap changes.',
      do: async () => {
        await card('leg', { x: 640, y: 500, w: 270, icon: 'piece', k: 'The salary system\'s piece', r: 'Leg', c: 'recipe' });
        await settle();
        await arrow('leg-gap', 'leg', 'gap', { label: 'fills' });
        await travel(['r12', 'r2g', 'rg4'], 1200);
      },
    },
    // ------------------------------------------------------------------ 10
    {
      say: 'Now imagine the cookbook is shared by many restaurants in a franchise. Each restaurant has its own space that only its own people can see. In the studio, each bank is a tenant.',
      do: async () => {
        await chapter('10', 'Franchise restaurants', 'Tenants, configuration and hooks');
        await card('cb', { x: 480, y: 120, w: 320, icon: 'books', k: 'The shared cookbook', r: 'Shared content', c: 'recipe' });
        for (const [i, n] of ['Bank A', 'Bank B', 'Bank C'].entries()) await card(`b${i}`, { x: 120 + i * 370, y: 300, w: 300, icon: 'store', k: n, r: 'Tenant', c: 'bank' });
        await settle();
        for (const i of [0, 1, 2]) await arrow(`cb-b${i}`, 'cb', `b${i}`);
      },
    },
    {
      say: 'A restaurant cannot rewrite the shared recipes. But it can set its own dials: which partners it works with, and its own limits. These settings are its configuration.',
      do: async () => { await card('cfg', { x: 120, y: 470, w: 330, icon: 'dial', k: 'Its own dials', r: 'Configuration', d: 'Partners it works with, its limits.', c: 'bank' }); },
    },
    {
      say: 'The house recipe also has marked spots where a restaurant may add a step of its own, like its house garnish before a dish goes out. These spots are hook points, and the restaurant\'s own step is a hook.',
      do: async () => { await card('hk', { x: 490, y: 470, w: 330, icon: 'plug', k: 'Its own extra step', r: 'Hook', d: 'Added at a marked spot.', c: 'bank' }); },
    },
    {
      say: 'A hook can hold an order for a person to approve, add a detail, or simply let someone know. It can never change the shared recipe itself.',
      do: async () => { await note('hn', 'A hook may <b>hold</b>, <b>add a detail</b> or <b>tell someone</b>. It never edits the shared recipe.', { x: 860, y: 470, w: 380 }); },
    },
    // ------------------------------------------------------------------ 11
    {
      say: 'Not everyone may change a recipe. People have roles: some write, some approve, and some only read. And the person who writes a change never approves it. A second person checks it. This is called maker and checker.',
      do: async () => {
        await chapter('11', 'Who may change what', 'Roles, maker and checker, editions, change sets, the log');
        await card('mc', { x: 60, y: 140, w: 340, icon: 'people', k: 'Two people', r: 'Maker and checker', d: 'One writes, another approves.', c: 'safety' });
      },
    },
    {
      say: 'Every recipe card is printed in numbered editions. A printed edition is never changed: a change means a new edition. Orders already cooking keep the edition they started with.',
      do: async () => {
        await card('ed', { x: 470, y: 140, w: 340, icon: 'books', k: 'Numbered editions', r: 'Releases', d: 'Never changed once printed.', c: 'safety' });
        for (const [i, v] of ['1.0.0', '1.0.1', '2.0.0'].entries()) await pill(`v${i}`, `edition ${v}`, { x: 480 + i * 115, y: 300, c: 'safety' });
      },
    },
    {
      say: 'Some recipes depend on each other, like a dish and its sauce. They are approved together, as one change set, so the kitchen never has one without the other.',
      do: async () => { await card('cs', { x: 880, y: 140, w: 340, icon: 'chain', k: 'Approved together', r: 'Change set', d: 'A dish and its sauce.', c: 'safety' }); },
    },
    {
      say: 'And every change, every approval and every refusal is written in the kitchen log. The log cannot be quietly edited.',
      do: async () => { await card('lg', { x: 470, y: 390, w: 340, icon: 'log', k: 'The kitchen log', r: 'Audit log', d: 'Every change, approval and refusal.', c: 'safety' }); },
    },
    // ------------------------------------------------------------------ 12
    {
      say: 'Before a recipe reaches the restaurant, it is tried in the test kitchen with the stand-in suppliers. A test says: given this order, and these stand-ins, I expect this result.',
      do: async () => {
        await chapter('12', 'Tasting before service', 'Tests, the virtual clock, traces and the conformance kit');
        await card('ts', { x: 60, y: 140, w: 360, icon: 'report', k: 'A tasting', r: 'Test', d: 'This order, these stand-ins, this result.', c: 'test' });
      },
    },
    {
      say: 'The test kitchen has a fast-forward clock. If the recipe says wait two days for an answer, the test skips straight to it. So long waits take a moment.',
      do: async () => { await card('vc', { x: 470, y: 140, w: 340, icon: 'watch', k: 'A fast-forward clock', r: 'Virtual clock', c: 'test' }); },
    },
    {
      say: 'Every test prints a trip report: one line for each station, saying how each step ended. If the report is not what was expected, the test fails.',
      do: async () => {
        for (const [i, t] of ['check: pass', 'reserve: reserved', 'send: accepted', 'end: settled'].entries()) await pill(`t${i}`, t, { x: 60 + i * 210, y: 330, c: 'test' });
        await note('tr', 'The trip report is called the <b>trace</b>.', { x: 60, y: 400, w: 500 });
      },
    },
    {
      say: 'There is also a fixed set of tastings that every real kitchen must pass, called the conformance kit. It proves that each engine cooks a recipe exactly the way the studio does.',
      do: async () => { await card('ck', { x: 880, y: 140, w: 340, icon: 'check', k: 'The same dish everywhere', r: 'Conformance kit', c: 'test' }); },
    },
    // ------------------------------------------------------------------ 13
    {
      say: 'When the recipes are approved, they are packed up for the real kitchen. This is called the export.',
      do: async () => {
        await chapter('13', 'Service', 'Export, the engine and its workers');
        await card('crate', { x: 60, y: 260, w: 300, icon: 'crate', k: 'Packed for service', r: 'Export', c: 'run' });
      },
    },
    {
      say: 'In the real kitchen, the engine runs every order. The work is shared between cooks, each at their own station, doing only their own jobs: one for the house recipe, one for each partner, and one for the restaurant\'s own steps. These cooks are called workers.',
      do: async () => {
        await card('eng', { x: 470, y: 260, w: 300, icon: 'gear', k: 'The real kitchen', r: 'Engine', c: 'run' });
        const cooks: [string, string][] = [['House recipe', 'platform'], ['Salary partner', 'scheme'], ['Instant partner', 'scheme'], ['The bank\'s own', 'bank']];
        for (const [i, [k, r]] of cooks.entries()) await card(`ck${i}`, { x: 900, y: 120 + i * 120, w: 320, icon: 'hat', k, r: `worker: ${r}`, c: 'run' });
        await settle();
        await arrow('cr-en', 'crate', 'eng');
        for (const i of [0, 1, 2, 3]) await arrow(`en-ck${i}`, 'eng', `ck${i}`);
      },
    },
    {
      say: 'Because each worker does only its own jobs, a problem in one partner\'s part cannot touch another\'s.',
      do: async () => { await light('ck1'); await d.wait(1200); await unlight('ck1'); },
    },
    // ------------------------------------------------------------------ 14
    {
      say: 'Let us put it all together.',
      do: async () => { await chapter('14', 'Everything together', ''); },
    },
    {
      say: 'An order arrives through a partner\'s order desk, the dispatch table. A translator, the map, writes it onto our standard ticket. The desk starts the house recipe, the platform workflow.',
      do: async () => {
        await card('A', { x: 40, y: 120, w: 220, icon: 'ticket', k: 'An order', r: 'File', c: 'ticket' });
        await card('B', { x: 300, y: 120, w: 220, icon: 'desk', k: 'Order desk', r: 'Dispatch table', c: 'scheme' });
        await card('C', { x: 560, y: 120, w: 220, icon: 'talk', k: 'Translator', r: 'Map', c: 'door' });
        await card('D', { x: 820, y: 120, w: 260, icon: 'recipe', k: 'House recipe', r: 'Platform workflow', c: 'recipe' });
        await settle();
        await arrow('AB', 'A', 'B'); await arrow('BC', 'B', 'C'); await arrow('CD', 'C', 'D');
      },
    },
    {
      say: 'The recipe\'s stations are activities, and its checking stations follow checklists of rules. In its gap, the partner\'s leg does the partner\'s part, talking through a door, the port, to the partner, the scheme.',
      do: async () => {
        await card('E', { x: 40, y: 285, w: 220, icon: 'check', k: 'Checklists', r: 'Rules', c: 'rule' });
        await card('F', { x: 300, y: 285, w: 220, icon: 'pot', k: 'Stations', r: 'Activities', c: 'station' });
        await card('G', { x: 560, y: 285, w: 220, icon: 'piece', k: 'Partner\'s piece', r: 'Leg', c: 'recipe' });
        await card('H', { x: 820, y: 285, w: 200, icon: 'door', k: 'Door', r: 'Port', c: 'door' });
        await card('I', { x: 1060, y: 285, w: 190, icon: 'network', k: 'Partner', r: 'Scheme', c: 'scheme' });
        await settle();
        await arrow('EF', 'E', 'F'); await arrow('DG', 'D', 'G'); await arrow('GH', 'G', 'H'); await arrow('HI', 'H', 'I');
      },
    },
    {
      say: 'Each bank sets its own dials, its configuration, and adds its own hooks. Everything is tried first in the test kitchen with stand-ins, approved by a second person, and then exported to the engine, where the workers cook it for real.',
      do: async () => {
        await card('J', { x: 40, y: 450, w: 220, icon: 'dial', k: 'Bank dials', r: 'Configuration', c: 'bank' });
        await card('L', { x: 300, y: 450, w: 220, icon: 'plug', k: 'Bank steps', r: 'Hooks', c: 'bank' });
        await card('M', { x: 560, y: 450, w: 220, icon: 'report', k: 'Tasting', r: 'Tests', c: 'test' });
        await card('N', { x: 820, y: 450, w: 200, icon: 'crate', k: 'Packed', r: 'Export', c: 'run' });
        await card('O', { x: 1060, y: 450, w: 190, icon: 'gear', k: 'Service', r: 'Engine', c: 'run' });
        await settle();
        await arrow('MN', 'M', 'N'); await arrow('NO', 'N', 'O');
        await travel(['AB', 'BC', 'CD', 'DG', 'GH', 'HI'], 1100);
      },
    },
    {
      card: ['That is every idea', 'Next: part 2, building each of these pieces in the studio, step by step.'],
      say: 'That is every idea in the studio. In the next video, we will build each of these pieces in the studio itself, step by step: a rule, an activity, a workflow, and a bank\'s own rules and hooks.',
    },
  ];
}
