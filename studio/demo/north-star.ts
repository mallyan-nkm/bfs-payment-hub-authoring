import { spawnSync } from 'node:child_process';
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';
import { SLOW, type Director, type Scene } from './record.js';
import { STUDIO } from '../test/blackbox/server.js';

// Video 3, the north star: the architecture drawn as one diagram (demo/explainer/ideas.html), then each north-star tenet in turn,
// what it means in plain words, how the system does it, and something real on screen: the studio on Fluent UI 2, a PostgreSQL
// store with one synthetic salary file in it (demo/north-star/store.sh), and the same conformance scenarios run on all four engines.
// It replaces the drawn runtime video 2. Every claim is from the code and the docs of 24 November 2026
// (docs/north-star/architecture-essentials.md, docs/north-star/design/components.md, docs/runtime/PACKAGING-AND-DEPLOYMENT.md,
// docs/north-star/guidelines/access-control.md, docs/OWNER-ACTIONS.md).
//
// Before recording: `demo/north-star/store.sh up` (Docker), and the engines' kits ready to run (.NET 8 for DTFx and Elsa, the
// Temporal test server's classpath, the Flowable harness: CLAUDE.md). After: `demo/north-star/store.sh down`.

export const NAME = '3-north-star';
export const TITLE = 'TransactHub: the north star, tenet by tenet';
export const PACE = SLOW;

const PAGE = pathToFileURL(path.join(STUDIO, 'demo', 'explainer', 'ideas.html')).href;
const STORE = process.env.TH_DEMO_STORE ?? 'th-nstar-store';
const ROOT = path.resolve(STUDIO, '..');
const DRY = process.env.TH_DEMO_DRY === '1';
// The same few scenarios on every engine; a dry run runs one.
const KIT = DRY ? ['CK-SEQ-01'] : ['CK-SEQ-01', 'CK-SWITCH-01', 'CK-PAR-01', 'CK-RETRY-01', 'CK-AWAIT-01', 'CK-SLOT-01', 'CK-HOOK-01', 'CK-PIN-01'];

/** A query on the store, as the runtime's login (read and insert only), and what psql printed. */
function psql(sql: string, expanded = false): string {
  const r = spawnSync('docker', ['exec', STORE, 'psql', '-U', 'th_runtime', '-d', 'th_store', '-P', 'pager=off', ...(expanded ? ['-x'] : []), '-c', sql], { encoding: 'utf8' });
  const out = `${r.stdout ?? ''}${r.stderr ?? ''}`.trimEnd();
  if (/No such container|is not running/.test(out)) throw new Error(`The store ${STORE} is not up: run demo/north-star/store.sh up`);
  return out;
}
/** Whether the video's store is up (demo/north-star/store.sh up): the dry run in the tests needs it. */
export function storeUp(): boolean {
  return spawnSync('docker', ['exec', STORE, 'pg_isready', '-q'], { encoding: 'utf8' }).status === 0;
}
/** A command run in the repository, and what it printed. */
function sh(command: string, cwd = ROOT): string {
  const r = spawnSync('bash', ['-c', command], { cwd, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 });
  return `${r.stdout ?? ''}${r.stderr ?? ''}`.trimEnd();
}
/** The kit on one engine, with the lines it printed that say what passed. */
function kit(engine: string): string {
  const out = sh(`node_modules/.bin/tsx engines/${engine}/run-kit.ts ${KIT.join(' ')}`, STUDIO);
  const lines = out.replace(/\x1b\[[0-9;]*m/g, '').split('\n').filter((l) => /^(PASS|FAIL)\s|conformance scenarios pass/.test(l));
  if (!lines.some((l) => /conformance scenarios pass/.test(l))) throw new Error(`The kit did not run on ${engine}:\n${out.slice(-2000)}`);
  if (lines.some((l) => l.startsWith('FAIL'))) throw new Error(`The kit failed on ${engine}:\n${lines.join('\n')}`);
  return lines.join('\n');
}

export async function open(d: Director) {
  await d.page.goto(PAGE);
  await d.page.evaluate(() => (window as any).K.bar('the north star, tenet by tenet'));
}

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');
  const panel = (title: RegExp) => main().locator('.panel', { has: p().locator('h3', { hasText: title }) }).first();
  const K = (js: string) => p().evaluate(js);
  const settle = () => d.wait(800);
  const j = JSON.stringify;
  const card = (id: string, o: Record<string, unknown>) => K(`K.card(${j(id)}, ${j(o)})`);
  const small = (id: string, o: Record<string, unknown>) => card(id, { ...o, size: 's' });
  const arrow = (id: string, a: string, b: string, o: Record<string, unknown> = {}) => K(`K.arrow(${j(id)}, ${j(a)}, ${j(b)}, ${j(o)})`);
  const note = (id: string, text: string, o: Record<string, unknown>) => K(`K.note(${j(id)}, ${j(text)}, ${j(o)})`);
  const pill = (id: string, text: string, o: Record<string, unknown>) => K(`K.pill(${j(id)}, ${j(text)}, ${j(o)})`);
  const chapter = (n: string, t: string, s = '') => K(`K.chapter(${j(n)}, ${j(t)}, ${j(s)})`);
  const travel = (ids: string[], ms = 1200) => K(`K.travel(${j(ids)}, ${ms})`);
  const light = (...ids: string[]) => K(`K.light(...${j(ids)})`);
  const unlight = (...ids: string[]) => K(`K.unlight(...${j(ids)})`);
  const dim = (...ids: string[]) => K(`K.dim(...${j(ids)})`);
  const remove = (...ids: string[]) => K(`K.remove(...${j(ids)})`);
  const hide = () => K('K.hideTicket()');
  const state = async (id: string, text: string, x: number, y: number, c: string) => { await remove(`${id}:s`); await pill(`${id}:s`, text, { x, y, c }); };
  /** Back to the explainer stage from a studio page. */
  const stage = async () => {
    await d.closeTerminal();
    if (!p().url().startsWith('file:')) await open(d);
  };
  /** A query on the store, shown in the terminal panel. */
  const query = async (sql: string, ms = 0, expanded = false) => {
    await d.terminal(`psql -U th_runtime -d th_store${expanded ? ' -x' : ''} -c '${sql.replace(/\s+/g, ' ').trim()}'`, psql(sql, expanded));
    await d.wait(ms);
  };

  // ------------------------------------------------------------------ the diagram
  const BOXES = ['studio', 'sidp', 'gh', 'dist', 'zone', 'fapi', 'host', 'work', 'ports', 'store', 'read', 'idp', 'auth', 'cons', 'out'];
  const parts = {
    authoring: async () => {
      await small('studio', { x: 24, y: 206, w: 270, icon: 'blueprint', k: 'Designer studio', r: 'multi-tenant', d: 'Workflows, rules, values, hooks. Tests. Maker-checker.', c: 'recipe' });
      await small('sidp', { x: 24, y: 116, w: 270, icon: 'shield', k: 'Vendor\'s identity provider', r: 'studio sign-in', c: 'safety' });
      await small('gh', { x: 24, y: 320, w: 270, icon: 'branch', k: 'GitHub content branch', r: 'the studio\'s only store', d: 'content-configurations/', c: 'safety' });
    },
    authoringArrows: async () => { await arrow('i-s', 'sidp', 'studio'); await arrow('s-g', 'studio', 'gh'); },
    dist: async () => {
      await small('dist', { x: 24, y: 440, w: 270, icon: 'crate', k: 'Distribution and configuration release', r: 'signed, per bank', c: 'recipe' });
      await K(`K.zone('zone', 'The runtime of one bank (TH_BANK)', { x: 322, y: 110, w: 936, h: 500, c: 'run' })`);
    },
    distArrows: async () => { await arrow('g-d', 'gh', 'dist'); await arrow('d-z', 'dist', 'zone', { label: 'installed with consent', side: 'h' }); },
    flow: async () => {
      await small('fapi', { x: 344, y: 138, w: 280, icon: 'door', k: 'File API and front door', r: 'every file in and out', d: 'Pins, starts or correlates a payment.', c: 'door' });
      await small('host', { x: 656, y: 138, w: 280, icon: 'gear', k: 'Engine host', r: 'one of four engines', d: 'Temporal, DTFx, Elsa or Flowable. A host per distribution.', c: 'run' });
      await small('work', { x: 968, y: 138, w: 268, icon: 'piece', k: 'Workers and native modules', r: 'activities', d: 'Each distribution\'s own build.', c: 'station' });
    },
    flowArrows: async () => { await arrow('f-h', 'fapi', 'host'); await arrow('h-w', 'host', 'work'); },
    data: async () => {
      await small('ports', { x: 344, y: 290, w: 280, icon: 'plug', k: 'Ports and adapters', r: 'circuit breakers', d: 'Core banking, SFTP, screening, events, alerts.', c: 'door' });
      await small('store', { x: 656, y: 290, w: 280, icon: 'db', k: 'Store', r: 'insert-only', d: 'SQL Server or PostgreSQL. ISO 20022 tables.', c: 'run' });
      await small('read', { x: 968, y: 290, w: 268, icon: 'report', k: 'Read model', r: 'fed by the outbox', c: 'run' });
      await small('cons', { x: 968, y: 430, w: 268, icon: 'screen', k: 'Consoles', r: 'operations, service', d: 'And the test bench.', c: 'bank' });
    },
    dataArrows: async () => { await arrow('h-s', 'host', 'store'); await arrow('s-r', 'store', 'read'); await arrow('r-c', 'read', 'cons'); },
    edge: async () => {
      await small('idp', { x: 344, y: 430, w: 280, icon: 'shield', k: 'Bank\'s identity provider', r: 'OIDC, role map', c: 'bank' });
      await small('auth', { x: 656, y: 430, w: 280, icon: 'key', k: 'Machine auth', r: 'client credentials', d: 'Services prove who they are.', c: 'safety' });
      await small('out', { x: 344, y: 540, w: 892, icon: 'network', k: 'Partners: the Central Bank\'s SFTP, core banking, screening, Kafka, MQTT', c: 'scheme' });
    },
  };
  /** The whole diagram, drawn again; the parts a tenet is about are lit, the rest dimmed. */
  const diagram = async (n: string, title: string, sub: string, lit: string[] = []) => {
    await stage();
    await chapter(n, title, sub);
    for (const f of Object.values(parts)) await f();
    if (lit.length) {
      await d.wait(700);
      await dim(...BOXES.filter((b) => !lit.includes(b)));
      await light(...lit.filter((b) => b !== 'zone'));
    }
  };

  return [
    // ------------------------------------------------------------------ 0
    {
      card: ['TransactHub: the north star', 'The whole picture, then each tenet: what it means, and how the system does it.'],
      say: 'This video walks through the north-star architecture of TransactHub: first the whole picture, then each tenet in turn: what it means, how the system does it, and where you can see it.',
    },
    // ------------------------------------------------------------------ 1. the picture
    {
      say: 'On the left is the authoring side. The designer studio is where workflows, rules, values and hooks are written, tested and approved. People sign in through the vendor\'s identity provider, and the studio\'s only store is a GitHub branch.',
      do: async () => {
        await chapter('1', 'The whole picture', 'The studio, GitHub, and the runtime of one bank');
        await parts.authoring();
        await settle();
        await parts.authoringArrows();
      },
    },
    {
      say: 'When a bank\'s change is approved, the studio builds and signs its distribution and its configuration release. The bank installs them in its own runtime, with a bank approver\'s consent.',
      do: async () => { await parts.dist(); await settle(); await parts.distArrows(); },
    },
    {
      say: 'In the runtime, every file comes and goes through the File API. Its front door starts a payment, or hands a message to the payment that waits for it. The engine host runs the workflow on one of four engines, and workers run the activities.',
      do: async () => { await parts.flow(); await settle(); await parts.flowArrows(); },
    },
    {
      say: 'Everything is written to one insert-only store, on SQL Server or PostgreSQL. A read model, fed from the store\'s outbox, serves the consoles.',
      do: async () => { await parts.data(); await settle(); await parts.dataArrows(); },
    },
    {
      say: 'Outside systems are reached through ports and adapters. People sign in through the bank\'s identity provider, and services use machine credentials.',
      do: async () => { await parts.edge(); },
    },
    {
      say: 'Ten tenets shape this picture. We take them one at a time, and come back to the diagram for each.',
      do: async () => {
        await dim(...BOXES);
        await note('tenets', ['ISO 20022 native', 'Platform workflow and scheme leg', 'Workflow independent of the engine', 'SQL Server and PostgreSQL', 'Insert-only, versioned, hot update', 'A bank is configuration, with consent', 'No-code and code', 'Files, ports and events', 'Authentication and personas', 'One bank per runtime'].map((t, i) => `<b>${i + 1}</b>&nbsp; ${t}`).join('<br>'), { x: 420, y: 118, w: 520 });
      },
    },
    // ------------------------------------------------------------------ 2. ISO 20022 native
    {
      say: 'Tenet one: ISO 20 022 native. ISO 20 022 is the international standard for payment messages. TransactHub uses it as its own language, in every workflow and in the database.',
      do: async () => { await diagram('2', 'ISO 20022 native', 'One model, inside and out', ['fapi', 'store']); },
    },
    {
      say: 'A scheme speaks its own format: a WPS salary file has a layout of its own. A map at the edge reads it into the canonical model, and another writes the reply out. Nothing in between knows the scheme\'s layout.',
      do: async () => {
        await chapter('2', 'ISO 20022 native', 'Scheme formats are mapped only at the edges');
        await card('sif', { x: 40, y: 150, w: 260, icon: 'file', k: 'A WPS salary file', r: 'scheme format', c: 'scheme' });
        await card('iso', { x: 470, y: 150, w: 340, icon: 'globe', k: 'The canonical model', r: 'ISO 20022', d: 'pain.001 and pacs.008 shapes: the workflow context and the store.', c: 'recipe' });
        await card('outf', { x: 980, y: 150, w: 260, icon: 'file', k: 'A file to the scheme', r: 'scheme format', c: 'scheme' });
        await settle();
        await arrow('m1', 'sif', 'iso', { label: 'intake map' });
        await arrow('m2', 'iso', 'outf', { label: 'map out' });
        await travel(['m1', 'm2']);
        await hide();
      },
    },
    {
      say: 'So every rule, workflow and report works on one shape. A new scheme brings maps and a leg, not new tables. A field ISO has no place for is a typed, registered extension.',
      do: async () => {
        await note('n1', 'Every rule, workflow and report works on <b>one shape</b>.<br>A new scheme brings maps and a leg: no new tables, no copy of the rules.<br>A field ISO has no place for is a <b>registered extension</b>, typed.', { x: 240, y: 360, w: 800 });
      },
    },
    {
      say: 'In the studio, the ISO data page is the dictionary: the canonical context, header first, then the payment, each element with its ISO type. Personal data is marked, so that it can be masked.',
      do: async () => {
        await d.go('data?tab=model');
        await d.point(main().getByText('header.paymentId').first(), 2200);
        await d.point(main().getByText('personal data').first(), 2000);
        await d.scroll(420);
      },
    },
    {
      say: 'Each scheme message has sample files, mapped into the model, and the broken ones are caught with their ISO reason codes.',
      do: async () => {
        await d.go('data?tab=samples');
        await d.point(main().locator('tr', { hasText: 'wps-sif-generated-valid.sif' }).first(), 2000);
        await d.point(main().locator('tr', { hasText: 'wps-sif-generated-bad-total.sif' }).first(), 2200);
      },
    },
    {
      say: 'Now a real PostgreSQL store, built by the runtime\'s own migrations, with one synthetic salary file in it. These are all its tables. No table per scheme, and none per bank.',
      do: async () => {
        await stage();
        await chapter('2', 'ISO 20022 native', 'In the store');
        await query('\\dt');
      },
    },
    {
      say: 'The salary file is one bulk, shaped like an ISO pain dot zero zero one: message id, salary purpose, transaction count, control sum, and the debtor\'s account and agent. WPS\'s own fields are typed supplementary data.',
      do: async () => {
        await query('SELECT "MessageId", "CategoryPurposeCode" AS purpose, "NumberOfTransactions" AS txs, "ControlSum", debtor_account_iban, debtor_agent_clearing_system AS clearing, debtor_agent_member_id AS member, "SupplementaryJson", "ReleaseId" FROM payment_bulk', 0, true);
      },
    },
    {
      say: 'Each employee\'s line is a payment: end-to-end id, amount, currency, purpose, and the creditor\'s account. Scheme and bank are columns, not tables.',
      do: async () => {
        await query('SELECT "LineNo", "Tenant", "Scheme", "EndToEndId", "Amount", "Currency", "PurposeCode", creditor_account_iban, "SupplementaryJson" FROM payment ORDER BY "LineNo"', 0, true);
      },
    },
    {
      say: 'And the facts. Each step carries the workflow, the step, the activity and its release, but no names and no account numbers. The context carries ids, so the engine\'s history holds no personal data.',
      do: async () => {
        await query('SELECT "Seq", "Kind", "Code", "DataJson" FROM payment_fact WHERE "Kind" = \'step\' ORDER BY "Seq"');
      },
    },
    // ------------------------------------------------------------------ 3. platform workflow and scheme leg
    {
      say: 'Tenet two: a platform workflow and a scheme leg. The life of a payment is written once, by the platform. A scheme fills only its own part.',
      do: async () => { await diagram('3', 'Platform workflow and scheme leg', 'Written once; a scheme fills the slot', ['studio', 'host']); },
    },
    {
      say: 'The corporate workflow checks, screens, checks limits, reserves the funds and routes. Then comes a slot, the scheme leg, with a contract: what the leg reads, what it writes, and how it may end. Then the platform posts and closes.',
      do: async () => {
        await chapter('3', 'Platform workflow and scheme leg', 'p.wf.corporate.v1 and its slot');
        const steps: [string, string, string][] = [['c1', 'Check and screen', 'station'], ['c2', 'Limits', 'station'], ['c3', 'Reserve', 'station'], ['c4', 'Route', 'station']];
        for (const [i, [id, k, c]] of steps.entries()) await small(id, { x: 40 + i * 175, y: 150, w: 155, icon: 'check', k, c });
        await small('slot', { x: 740, y: 150, w: 190, icon: 'gap', k: 'Slot: scheme-leg', r: 'a contract', c: 'safety' });
        await small('c6', { x: 960, y: 150, w: 130, icon: 'log', k: 'Post', c: 'station' });
        await small('c7', { x: 1110, y: 150, w: 130, icon: 'talk', k: 'Close', c: 'station' });
        await settle();
        for (const [a, b] of [['c1', 'c2'], ['c2', 'c3'], ['c3', 'c4'], ['c4', 'slot'], ['slot', 'c6'], ['c6', 'c7']]) await arrow(`${a}-${b}`, a, b);
        await note('ct', 'The slot\'s contract: reads the payment, the route and the reservation; writes the status, the settlement and messages; ends settled, rejected, returned or pending.', { x: 160, y: 250, w: 960 });
      },
    },
    {
      say: 'WPS is a leg: it builds the salary file, sends it, and waits for the acknowledgement. The dispatch table says which message starts which payment, with which map and which leg. Refunds work the same way.',
      do: async () => {
        await card('leg', { x: 600, y: 400, w: 470, icon: 'piece', k: 'The WPS leg', r: 'uae.wps.wf.salary-file-out.v1', d: 'Builds the file, sends it, waits for the acknowledgement.', c: 'scheme' });
        await card('disp', { x: 60, y: 400, w: 470, icon: 'desk', k: 'The dispatch table', r: 'scheme uae.wps', d: 'A salary file starts an outward corporate payment, with its intake map and its leg.', c: 'scheme' });
        await settle();
        await arrow('dl', 'disp', 'leg');
        await arrow('ls', 'leg', 'slot', { label: 'fills' });
        await light('slot');
      },
    },
    {
      say: 'Here is the corporate workflow in the designer: its text on the left, its steps on the right. The slot waits for a leg, and the hook points wait for a bank.',
      do: async () => {
        await d.go('designer');
        await d.select(p().getByLabel('Published workflow'), { label: 'p.wf.corporate.v1 @1.0.0' });
        await d.click(p().getByRole('button', { name: 'Open', exact: true }).last());
        await p().waitForTimeout(800);
        await d.point(main().locator('.cv-steps').first(), 3000);
        await d.point(main().locator('.cv-steps').getByText('scheme-leg').first(), 2500);
      },
    },
    {
      say: 'And the WPS dispatch table. A salary file starts an outward corporate payment, with its leg and its intake map. An acknowledgement goes to the payment that waits for it. A leg that does not fit its slot is refused.',
      do: async () => {
        await d.go('editors?kind=scheme&open=' + encodeURIComponent('scheme:uae.wps'));
        const table = panel(/^Scheme and dispatch table/);
        await d.point(table.locator('tr', { hasText: 'uae.wps.sif' }).first(), 3500);
        await d.point(table.locator('tr', { hasText: 'uae.wps.ack' }).first(), 2500);
      },
    },
    // ------------------------------------------------------------------ 4. workflow independent of the engine
    {
      say: 'Tenet three: the workflow does not depend on the engine. TransactHub supports four engines: Temporal, the default, DTFx, Elsa and Flowable.',
      do: async () => { await diagram('4', 'Workflow independent of the engine', 'One workflow, four engines', ['host']); },
    },
    {
      say: 'The studio exports one expanded workflow, and one interpreter runs it on every engine, behind one small contract. The engine keeps the state and the timers.',
      do: async () => {
        await chapter('4', 'Workflow independent of the engine', 'One interpreter behind one contract');
        await card('wf', { x: 450, y: 130, w: 380, icon: 'recipe', k: 'The expanded workflow', r: 'one interpreter', d: 'Behind one contract: IEngine.', c: 'recipe' });
        const engines: [string, string, string][] = [['tmp', 'Temporal', 'The default.'], ['dtfx', 'DTFx', 'A library on SQL Server.'], ['elsa', 'Elsa 3', 'The interpreter in one activity.'], ['flw', 'Flowable 7', 'BPMN, external workers.']];
        for (const [i, [id, k, dd]] of engines.entries()) await card(id, { x: 40 + i * 310, y: 340, w: 280, icon: 'gear', k, r: 'engine', d: dd, c: id === 'tmp' ? 'run' : 'safety' });
        await settle();
        for (const [id] of engines) await arrow(`e-${id}`, 'wf', id);
      },
    },
    {
      say: 'The export previews the package: its manifest, the pinned releases with their hashes, and one worker for each task queue.',
      do: async () => {
        await d.go('export?workflows=p.wf.corporate.v1&target=temporal');
        await d.click(p().getByRole('button', { name: 'Preview' }));
        await p().waitForTimeout(1200);
        await d.point(panel(/^Package/), 3500);
      },
    },
    {
      say: 'For Flowable, the same workflow is exported as BPMN. The workflow is untouched. Only the package changes.',
      do: async () => {
        await d.click(p().getByRole('radio', { name: 'flowable' }));
        await d.click(p().getByRole('button', { name: 'Preview' }));
        await p().waitForTimeout(1200);
        await d.click(p().getByRole('tab', { name: /^Files/ }));
        await d.point(main().locator('tr', { hasText: '.bpmn' }).first(), 3000);
      },
    },
    {
      say: 'The conformance kit proves that the engines agree: fifty-nine scenarios, each with the trace it must produce. Here it runs on the studio\'s interpreter.',
      do: async () => {
        await d.go('kit');
        await d.click(p().getByRole('button', { name: /^Run all/ }));
        await p().getByText(/\d+ of \d+ pass/).first().waitFor({ timeout: 120_000 });
        await d.point(p().getByText(/\d+ of \d+ pass/).first(), 2500);
      },
    },
    {
      say: 'Now eight of those scenarios, run on DTFx and on Elsa.',
      do: async () => {
        let out = '';
        await d.skip('Eight scenarios on DTFx and on Elsa', async () => {
          out = `${kit('dtfx')}\n\n$ npx tsx engines/elsa/run-kit.ts ${KIT.join(' ')}\n\n${kit('elsa')}`;
        });
        await d.terminal(`npx tsx engines/dtfx/run-kit.ts ${KIT.join(' ')}`, out);
        await d.wait(5000);
      },
    },
    {
      say: 'All eight pass. The whole kit passed on both this week. Now Temporal and Flowable.',
      do: async () => {
        await d.wait(3000);
        let out = '';
        await d.skip('The same eight on Temporal and on Flowable', async () => {
          out = `${kit('temporal')}\n\n$ npx tsx engines/flowable/run-kit.ts ${KIT.join(' ')}\n\n${kit('flowable')}`;
        });
        await d.terminal(`npx tsx engines/temporal/run-kit.ts ${KIT.join(' ')}`, out);
        await d.wait(5000);
      },
    },
    {
      say: 'They pass there too. Each installation runs one engine, and the workflows do not change with it.',
    },
    // ------------------------------------------------------------------ 5. SQL Server and PostgreSQL
    {
      say: 'Tenet four: SQL Server and PostgreSQL. The runtime behaves the same on either.',
      do: async () => { await diagram('5', 'SQL Server and PostgreSQL', 'One model, two databases', ['store']); },
    },
    {
      say: 'There is one store model, and the database is chosen in one place. A build test fails on a driver or raw SQL anywhere else, and every store test runs on both databases.',
      do: async () => {
        await chapter('5', 'SQL Server and PostgreSQL', 'Chosen in one place');
        await card('model', { x: 450, y: 130, w: 380, icon: 'db', k: 'One store model', r: 'chosen in one place', d: 'A build test refuses a driver or raw SQL anywhere else.', c: 'run' });
        await card('mssql', { x: 160, y: 340, w: 330, icon: 'db', k: 'SQL Server', r: 'its own migrations', c: 'safety' });
        await card('pg', { x: 790, y: 340, w: 330, icon: 'db', k: 'PostgreSQL', r: 'its own migrations', c: 'safety' });
        await settle();
        await arrow('mm', 'model', 'mssql'); await arrow('mp', 'model', 'pg');
        await pill('both', 'Every store test runs on both databases', { x: 470, y: 470, c: 'test' });
      },
    },
    {
      say: 'Each database has its own migrations. This video\'s store is PostgreSQL. The laptop stack runs on SQL Server by default, with a PostgreSQL version too.',
      do: async () => {
        await d.terminal('ls -d runtime/src/*Migrations* runtime/temporal-mssql deploy/laptop/compose.postgres.yaml', sh('ls -d runtime/src/*Migrations* runtime/temporal-mssql deploy/laptop/compose.postgres.yaml'));
        await d.wait(3000);
      },
    },
    // ------------------------------------------------------------------ 6. insert-only, versioned, hot update
    {
      say: 'Tenet five: insert-only, versioned, and updated while running. Nothing is changed or deleted. A new version is inserted beside the old one.',
      do: async () => { await diagram('6', 'Insert-only, versioned, hot update', 'Nothing is overwritten', ['store', 'dist']); },
    },
    {
      say: 'The salary file\'s history: twelve facts, each holding the hash of the one before. Change one, and the check names it. The status, settled, is worked out from the facts.',
      do: async () => {
        await stage();
        await chapter('6', 'Insert-only, versioned, hot update', 'In the store');
        await query('SELECT "Seq", "Kind", "Code", "Actor", to_char("At", \'HH24:MI\') AS at, left("PrevHash", 16) AS prev_hash, left("Hash", 16) AS hash FROM payment_fact WHERE "SubjectId" = (SELECT "Id" FROM payment_bulk) ORDER BY "Seq"');
      },
    },
    {
      say: 'The database enforces it. The runtime\'s login may read and insert, nothing more. An update is refused, and so is a delete.',
      do: async () => {
        const sql = ['UPDATE payment_fact SET "Code" = \'ACSC\' WHERE "Seq" = 1;', 'DELETE FROM payment_fact;'];
        await d.terminal(`psql -U th_runtime -d th_store -c '${sql[0]}' -c '${sql[1]}'`, `${psql(sql[0])}\n${psql(sql[1])}`);
        await d.wait(2000);
      },
    },
    {
      say: 'New logic arrives as a distribution: a complete, signed copy of what the bank runs, stored once. A payment is pinned to the distribution active at its acceptance, and finishes on it.',
      do: async () => {
        await stage();
        await chapter('6', 'Insert-only, versioned, hot update', 'Pinned, side by side, drained');
        await card('A', { x: 120, y: 140, w: 300, icon: 'crate', k: 'Distribution A', r: '2026.11.24-1', c: 'recipe' });
        await state('A', 'Active', 440, 172, 'var(--bank)');
        await card('p1', { x: 120, y: 340, w: 240, icon: 'ticket', k: 'The salary file', r: 'pinned to A', c: 'ticket' });
        await settle();
        await arrow('pa', 'p1', 'A', { label: 'pinned at acceptance' });
      },
    },
    {
      say: 'Activate B, and A drains in the same step. New payments take B, and payments on A run to their end. Then A can be removed. Rollback is activating A again.',
      do: async () => {
        await card('B', { x: 760, y: 140, w: 300, icon: 'crate', k: 'Distribution B', r: '2026.11.24-2', c: 'recipe' });
        await state('B', 'Installed', 1080, 172, 'var(--safety)');
        await settle();
        await state('B', 'Active', 1080, 172, 'var(--bank)');
        await state('A', 'Draining', 440, 172, 'var(--rule)');
        await card('p2', { x: 760, y: 340, w: 240, icon: 'ticket', k: 'A new payment', r: 'pinned to B', c: 'ticket' });
        await settle();
        await arrow('pb', 'p2', 'B', { label: 'pinned at acceptance' });
        await note('st', 'Installed, Active, Draining, Drained, Removed. Each change is a new row, with who did it and when.', { x: 240, y: 500, w: 800 });
      },
    },
    {
      say: 'In the store, each step is a fact: A installed and active, the salary file pinned to A, then B installed and active, and A draining. Each with who did it.',
      do: async () => {
        await query('SELECT "SubjectId", "Seq", "Kind", "Code", "Actor", to_char("At", \'HH24:MI\') AS at FROM payment_fact WHERE "SubjectKind" = \'distribution\' OR "Kind" = \'distribution.pinned\' ORDER BY "Id"');
      },
    },
    {
      say: 'Values are different. They arrive in a signed configuration release, each one effective-dated: here the bank\'s limit is two hundred and fifty thousand this year, and one hundred thousand from January. Values can reach a payment in flight. Logic is pinned.',
      do: async () => {
        await query('SELECT "Name", "VersionNo", "BodyJson", "EffectiveFrom"::date AS valid_from, "EffectiveTo"::date AS valid_to, "PublishedBy" FROM versioned_value ORDER BY "Name", "VersionNo"');
      },
    },
    {
      say: 'Every fact also goes to the outbox, in the same transaction. The read model and the Kafka stream follow it, and the stream\'s position is a new row each time.',
      do: async () => {
        const q1 = 'SELECT "Id", "Topic", "Key", left("PayloadJson", 70) AS payload FROM outbox ORDER BY "Id" DESC LIMIT 6';
        const q2 = 'SELECT "Id", "Stream", "Through", "Events", "At" FROM stream_position';
        await d.terminal(`psql -U th_runtime -d th_store -c '${q1}' -c '${q2}'`, `${psql(q1)}\n\n${psql(q2)}`);
      },
    },
    // ------------------------------------------------------------------ 7. a bank is configuration
    {
      say: 'Tenet six: a bank is configuration, never a fork of the code: rules, values and hooks. Shared code never names a bank.',
      do: async () => { await diagram('7', 'A bank is configuration', 'Rules, values and hooks, with consent', ['studio', 'dist']); },
    },
    {
      say: 'This is the bank\'s workspace, signed in as its configurator: its hooks at the platform\'s hook points, its own rules, simulators and port bindings. Shared content is read-only.',
      do: async () => {
        await d.go('bank');
        await d.point(panel(/^Hooks/), 2500);
        await d.point(panel(/^Rules and rule values/), 2000);
      },
    },
    {
      say: 'And its configuration set. Every dial has a default and bounds. This limit may be lowered, never raised, and each value has its dates.',
      do: async () => {
        await d.go('tenant');
        await d.point(panel(/^Configuration set/), 2200);
        await d.point(main().locator('tr', { hasText: 'internalLimit' }).first(), 3000);
      },
    },
    {
      say: 'Nobody changes a bank alone. In the studio, the configurator makes a change, and a bank approver approves it. In the runtime, every change of the system needs a bank approver\'s consent.',
      do: async () => {
        await stage();
        await chapter('7', 'A bank is configuration', 'Two people for every change');
        await card('mk', { x: 60, y: 130, w: 330, icon: 'actor', k: 'Bank configurator', r: 'studio: makes', c: 'bank' });
        await card('ck', { x: 470, y: 130, w: 330, icon: 'shield', k: 'Bank approver', r: 'studio: approves', d: 'Never the maker.', c: 'bank' });
        await card('se', { x: 60, y: 320, w: 330, icon: 'actor', k: 'Service engineer', r: 'runtime: asks', d: 'With a reason.', c: 'run' });
        await card('ba', { x: 470, y: 320, w: 330, icon: 'shield', k: 'Bank approver', r: 'runtime: consents', c: 'bank' });
        await card('done', { x: 880, y: 320, w: 340, icon: 'gear', k: 'Carried out', r: 'a fact each step', c: 'safety' });
        await settle();
        await arrow('mc', 'mk', 'ck'); await arrow('sb', 'se', 'ba'); await arrow('bd', 'ba', 'done');
        await travel(['sb', 'bd']);
        await hide();
      },
    },
    {
      say: 'Installs, activations, drains, removals and rollbacks, a configuration in force, a restore, an unmask. Each request and each consent is a fact.',
      do: async () => {
        await note('cons', 'Install, activate, drain, remove, roll back. A configuration release in force. Restore. Unmask. A register change. <b>Each request and each consent is a fact.</b>', { x: 60, y: 480, w: 1160 });
      },
    },
    // ------------------------------------------------------------------ 8. no-code and code
    {
      say: 'Tenet seven: no code where it can be, code only where it must be. So, what is configuration, and what is code?',
      do: async () => { await diagram('8', 'No-code and code', 'What is configuration, what is code', ['studio', 'work']); },
    },
    {
      say: 'Configuration is what designers and configurators write in the studio, as data: workflows, rules, decision tables, values, hooks, reference tables and port bindings. It is tested and approved, with no developer.',
      do: async () => {
        await chapter('8', 'No-code and code', 'Configuration first');
        await card('cfg', { x: 40, y: 125, w: 590, icon: 'recipe', k: 'Configuration: no code', r: 'written in the studio as data', d: 'Workflows and slots. Rules, rule sets, decision tables. Values (dials). Hooks and hook bindings. Reference tables. Port bindings. Checked, tested, approved.', c: 'recipe' });
        await card('who1', { x: 40, y: 330, w: 590, icon: 'people', k: 'Who', r: 'designer, configurator', d: 'Workflow designers and bank configurators, with their approvers.', c: 'bank' });
      },
    },
    {
      say: 'Code is where configuration stops: the builtin services of the runtime, small scripts that see only an injected interface, native C# activities, and the adapters behind the ports.',
      do: async () => {
        await card('code', { x: 650, y: 125, w: 590, icon: 'gear', k: 'Code', r: 'only where it must be', d: 'Builtin services of the runtime. Script-behinds in TypeScript or Python, with only the injected interface. Native activities in C#. Adapters behind the ports.', c: 'safety' });
        await card('who2', { x: 650, y: 330, w: 590, icon: 'people', k: 'Who', r: 'developer, platform team', d: 'Workflow developers write scripts; the platform team writes native code and adapters.', c: 'safety' });
      },
    },
    {
      say: 'In the factory, counted over every release: builtin services, scripts, and those with a native implementation too.',
      do: async () => {
        const cmd = 'grep -rhoE "binding: (builtin|script|native)|native: true" content-configurations/artefacts | sort | uniq -c';
        await d.terminal(cmd, sh(cmd));
        await d.wait(2000);
      },
    },
    {
      say: 'Native code travels in the distribution, not in the runtime\'s image. Its hash is in the signed bill of materials, checked at install and at load, and each distribution loads its own build.',
      do: async () => {
        await stage();
        await chapter('8', 'No-code and code', 'Signed native modules, per distribution');
        const steps: [string, string, string, string][] = [['nb', 'Build', 'the studio', 'crate'], ['nr', 'Record', 'SHA-256, signed', 'lock'], ['nv', 'Verify', 'at install', 'shield'], ['nl', 'Load', 'one context per distribution', 'piece']];
        for (const [i, [id, k, r, icon]] of steps.entries()) await card(id, { x: 40 + i * 310, y: 220, w: 280, icon, k, r, c: i < 2 ? 'recipe' : 'run' });
        await settle();
        await arrow('n1', 'nb', 'nr'); await arrow('n2', 'nr', 'nv'); await arrow('n3', 'nv', 'nl');
        await note('nn', 'Two distributions side by side run two builds. A removed one is unloaded.', { x: 300, y: 400, w: 680 });
      },
    },
    // ------------------------------------------------------------------ 9. files, ports and events
    {
      say: 'Tenet eight: one door for files, and ports for everything else outside.',
      do: async () => { await diagram('9', 'Files, ports and events', 'One door for files; ports for the rest', ['fapi', 'ports', 'out']); },
    },
    {
      say: 'Every file comes in through the File API: uploads over HTTPS or on a screen, and polled inboxes, such as a partner\'s SFTP folder. It is checked, decrypted if needed, kept with its hash, and handed to the front door.',
      do: async () => {
        await chapter('9', 'Files, ports and events', 'The File API: the only door for files');
        await card('fa', { x: 470, y: 230, w: 340, icon: 'door', k: 'The File API', r: 'check, decrypt, keep', d: 'Each step a fact.', c: 'door' });
        await small('i1', { x: 40, y: 140, w: 300, icon: 'network', k: 'A channel\'s upload', r: 'HTTPS', c: 'scheme' });
        await small('i2', { x: 40, y: 250, w: 300, icon: 'actor', k: 'A person\'s upload', r: 'upload screen', c: 'scheme' });
        await small('i3', { x: 40, y: 360, w: 300, icon: 'lock', k: 'A partner\'s SFTP folder', r: 'polled inbox', c: 'scheme' });
        await card('fd', { x: 940, y: 150, w: 300, icon: 'desk', k: 'Front door', r: 'pin, start, correlate', c: 'recipe' });
        await settle();
        for (const i of ['i1', 'i2', 'i3']) await arrow(`${i}f`, i, 'fa', { side: 'h' });
        await arrow('ffd', 'fa', 'fd');
        await travel(['i3f', 'ffd']);
        await hide();
      },
    },
    {
      say: 'Going out, a workflow\'s file goes to the outbox, is kept with its payment, and is delivered by the bank\'s binding, such as SFTP to the Central Bank.',
      do: async () => {
        await card('ob', { x: 940, y: 380, w: 300, icon: 'file', k: 'Outbox, then delivery', r: 'sftp or directory', d: 'To the Central Bank.', c: 'door' });
        await settle();
        await arrow('fob', 'fa', 'ob');
      },
    },
    {
      say: 'Every other outside system is a port with adapters, chosen by a dated binding. A missing binding is refused. And each adapter has a circuit breaker.',
      do: async () => {
        await stage();
        await chapter('9', 'Files, ports and events', 'Ports, adapters and circuit breakers');
        await card('port', { x: 60, y: 130, w: 360, icon: 'plug', k: 'A port', r: 'p.port.core-banking.v1', c: 'door' });
        const ads: [string, string][] = [['a1', 'simulator'], ['a2', 'gateway-rest'], ['a3', 'file-bulk']];
        for (const [i, [id, k]] of ads.entries()) await small(id, { x: 520, y: 110 + i * 85, w: 260, icon: 'gear', k, r: 'adapter', c: 'safety' });
        await settle();
        for (const [id] of ads) await arrow(`p-${id}`, 'port', id, { side: 'h' });
        await card('cb', { x: 860, y: 130, w: 360, icon: 'shield', k: 'Circuit breaker', r: 'on every adapter', d: 'Thresholds are settings with defaults and bounds.', c: 'rule' });
        await note('pb', 'A bank chooses its adapter by an <b>effective-dated binding</b>. A missing binding is refused, not defaulted.', { x: 60, y: 400, w: 760 });
      },
    },
    {
      say: 'Payment events go to Kafka from the outbox: references only, in order for each payment. Alerts go out by MQTT.',
      do: async () => {
        await small('kafka', { x: 860, y: 400, w: 360, icon: 'network', k: 'Events to Kafka', r: 'p.port.event-stream.v1', d: 'From the outbox. References only, in order per payment.', c: 'scheme' });
        await small('mqtt', { x: 860, y: 510, w: 360, icon: 'talk', k: 'Alerts by MQTT', r: 'p.port.notification.v1', c: 'scheme' });
      },
    },
    {
      say: 'In the bank\'s port bindings, core banking moves from the simulator to the REST gateway on the first of December, by a binding alone.',
      do: async () => {
        await d.go('bank');
        const pb = panel(/^Port bindings/);
        await pb.scrollIntoViewIfNeeded();
        await d.point(pb, 3500);
      },
    },
    // ------------------------------------------------------------------ 10. authentication and personas
    {
      say: 'Tenet nine: who you are, and what you may do. There are two identity providers, and they never mix.',
      do: async () => { await diagram('10', 'Authentication and personas', 'Two identity providers, clear roles', ['sidp', 'idp', 'auth', 'cons']); },
    },
    {
      say: 'The studio signs people in through the vendor\'s identity provider, and its users are a file on the branch. The runtime keeps no users: people sign in through the bank\'s provider, and a role map gives them runtime roles.',
      do: async () => {
        await chapter('10', 'Authentication and personas', 'Two identity providers');
        await card('s1', { x: 60, y: 130, w: 540, icon: 'blueprint', k: 'The studio', r: 'the vendor\'s identity provider', d: 'Users and roles in governance/users.yaml, on the branch.', c: 'recipe' });
        await card('r1', { x: 680, y: 130, w: 540, icon: 'store', k: 'The runtime', r: 'the bank\'s identity provider', d: 'No users of its own. A role map turns the bank\'s groups into roles.', c: 'run' });
      },
    },
    {
      say: 'Services use client credentials from the bank\'s provider. The runtime never trusts the studio\'s sign-in, only the keys that sign its packages.',
      do: async () => {
        await card('m1', { x: 680, y: 330, w: 540, icon: 'key', k: 'Machine callers', r: 'client credentials', d: 'The pipeline, workers, the native host\'s callers.', c: 'safety' });
        await card('sg', { x: 60, y: 330, w: 540, icon: 'lock', k: 'The only link', r: 'package-signing keys', d: 'The runtime trusts the studio\'s signatures, never its sign-in.', c: 'safety' });
        await settle();
        await arrow('sgr', 'sg', 'm1', { label: 'signed packages', side: 'h' });
      },
    },
    {
      say: 'In the studio: the workflow designer, the workflow developer, the platform approver, the bank configurator and the bank approver, each for one bank, and the auditor.',
      do: async () => {
        await stage();
        await chapter('10', 'Authentication and personas', 'Who sees what');
        const studio: [string, string][] = [['Workflow designer', 'workflows, schemes'], ['Workflow developer', 'activities, scripts, ports'], ['Platform approver', 'approves shared content'], ['Bank configurator', 'one bank: hooks, rules, values'], ['Bank approver', 'one bank: approves'], ['Auditor', 'reads']];
        for (const [i, [k, r]] of studio.entries()) await small(`ps${i}`, { x: 40 + (i % 3) * 205, y: 160 + Math.floor(i / 3) * 105, w: 190, icon: 'actor', k, d: r, c: i < 3 ? 'recipe' : i < 5 ? 'bank' : 'safety' });
        await pill('psl', 'In the studio', { x: 40, y: 118, c: 'recipe' });
      },
    },
    {
      say: 'In the runtime: the operator and the supervisor in the operations console, the service engineer in the service console, and the bank approver, who consents to every change of the system.',
      do: async () => {
        const rt: [string, string, string][] = [['Operator', 'operations console: works payments', 'run'], ['Supervisor', 'operations console: second approver', 'run'], ['Service engineer', 'service console: health, installs, backups', 'safety'], ['Bank approver', 'service console: consents', 'bank']];
        for (const [i, [k, r, c]] of rt.entries()) await small(`pr${i}`, { x: 690 + (i % 2) * 280, y: 160 + Math.floor(i / 2) * 105, w: 265, icon: 'actor', k, d: r, c });
        await pill('prl', 'In the runtime', { x: 690, y: 118, c: 'run' });
        await note('mx', 'Maker-checker everywhere: a change becomes a version only when someone who did not make it approves it. In the operations console, an action such as cancel or resend waits for the second approver its view declares.', { x: 40, y: 400, w: 1200 });
      },
    },
    {
      say: 'Each studio user lands on their own surfaces. The bank configurator works in its own bank only. The menu only hides things: the server checks every call.',
      do: async () => {
        await p().goto(`${d.server.url}/auth/login`);
        await d.wait(600);
        // A click on the provider's page, as signInPage does it, so that the viewer sees the sign-in.
        await d.click(p().getByRole('button', { name: 'example.maker', exact: true }));
        await p().waitForURL((u: URL) => u.pathname === '/' && !u.searchParams.has('code'));
        await p().getByLabel('Signed in as').waitFor();
        d.user = 'example.maker';
        await d.wait(800);
        await d.point(p().locator('nav').first(), 2500);
        await d.point(p().getByLabel('Signed in as'), 2000);
      },
    },
    // ------------------------------------------------------------------ 11. one bank per runtime
    {
      say: 'Tenet ten: one studio for everyone, and one runtime for each bank.',
      do: async () => { await diagram('11', 'One bank per runtime', 'A multi-tenant studio; GitHub its only store', ['studio', 'gh', 'zone']); },
    },
    {
      say: 'The studio is multi-tenant. A runtime is told its bank at install, and refuses another bank\'s distribution, configuration, file or backup.',
      do: async () => {
        await chapter('11', 'One bank per runtime', 'Told its bank at install');
        await card('st', { x: 60, y: 140, w: 330, icon: 'blueprint', k: 'The studio', r: 'multi-tenant', d: 'Platform, schemes, and every bank, each in its tenant.', c: 'recipe' });
        await card('gh2', { x: 470, y: 140, w: 340, icon: 'branch', k: 'One GitHub branch', r: 'content-configurations/', c: 'safety' });
        await card('b1', { x: 900, y: 110, w: 330, icon: 'store', k: 'Runtime of bank 1', r: 'TH_BANK', c: 'run' });
        await card('b2', { x: 900, y: 290, w: 330, icon: 'store', k: 'Runtime of bank 2', r: 'TH_BANK', c: 'run' });
        await settle();
        await arrow('sg2', 'st', 'gh2');
        await arrow('gb1', 'gh2', 'b1', { label: 'bank 1 only' }); await arrow('gb2', 'gh2', 'b2', { label: 'bank 2 only' });
        await pill('x', 'Another bank\'s distribution: refused at verify, install and activate', { x: 380, y: 470, c: 'scheme' });
      },
    },
    {
      say: 'The studio keeps no database. It reads and writes one GitHub branch, and a published file is never rewritten.',
      do: async () => {
        await d.go('export');
        const gb = panel(/^Content branch/);
        await gb.scrollIntoViewIfNeeded();
        await d.point(gb, 4000);
      },
    },
    {
      say: 'On a laptop, one compose file runs the studio and one bank\'s runtime: the database, SFTP, the identity provider and machine auth, the APIs and consoles, the File API, the workers and the engine hosts.',
      do: async () => {
        await stage();
        await chapter('11', 'One bank per runtime', 'On a laptop');
        const cmd = 'python3 -c "import yaml; s = yaml.safe_load(open(\'deploy/laptop/compose.yaml\'))[\'services\']; print(len(s), \'services:\'); print(\'\\n\'.join(s))"';
        await d.terminal(cmd, sh(cmd));
        await d.wait(3000);
      },
    },
    // ------------------------------------------------------------------ closing
    {
      say: 'That is the north star. One ISO model, inside and out. Platform workflows, with scheme legs. One workflow on four engines. Two databases. Insert-only, versioned, and updated while running.',
      do: async () => {
        await diagram('12', 'The north star', 'Every tenet in one picture');
        await d.wait(1000);
        for (const group of [['fapi', 'store'], ['studio', 'host'], ['host', 'work'], ['store'], ['store', 'dist']]) {
          await light(...group);
          await d.wait(1600);
          await unlight(...group);
        }
      },
    },
    {
      say: 'Banks as configuration, with consent. Configuration before code. One door for files. Two identity providers, and clear personas. And one runtime for each bank.',
      do: async () => {
        for (const group of [['studio', 'dist'], ['studio', 'work'], ['fapi', 'ports', 'out'], ['sidp', 'idp', 'auth', 'cons'], ['gh', 'dist']]) {
          await light(...group);
          await d.wait(1500);
          await unlight(...group);
        }
      },
    },
    {
      card: ['That is the north star', 'The concept video explains the ideas; the runtime videos show the laptop stack and a full payment run.'],
      say: 'Thank you for watching.',
    },
  ];
}
