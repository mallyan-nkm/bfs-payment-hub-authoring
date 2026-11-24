import * as path from 'node:path';
import { pathToFileURL } from 'node:url';
import { SLOW, type Director, type Scene } from './record.js';
import { STUDIO } from '../test/blackbox/server.js';

// Runtime video 2: the north-star architecture, drawn, with no screens. The stage is the training explainer's
// (demo/explainer/ideas.html); each line of the voice adds the next piece. Every claim is from the code and the docs of
// 23 November 2026 (docs/north-star/architecture-essentials.md, docs/runtime/PACKAGING-AND-DEPLOYMENT.md,
// docs/runtime/PLATFORM-PARITY.md, spec Part 9); what is not built yet is said where it matters.

export const NAME = 'runtime-2-north-star-architecture';
export const TITLE = 'TransactHub runtime, part 2: the north-star architecture';
export const PACE = SLOW;

const PAGE = pathToFileURL(path.join(STUDIO, 'demo', 'explainer', 'ideas.html')).href;
export async function open(d: Director) {
  await d.page.goto(PAGE);
}

export function scenes(d: Director): Scene[] {
  const K = (js: string) => d.page.evaluate(js);
  const settle = () => d.wait(900);
  const j = JSON.stringify;
  const card = (id: string, o: Record<string, unknown>) => K(`K.card(${j(id)}, ${j(o)})`);
  const arrow = (id: string, a: string, b: string, o: Record<string, unknown> = {}) => K(`K.arrow(${j(id)}, ${j(a)}, ${j(b)}, ${j(o)})`);
  const note = (id: string, text: string, o: Record<string, unknown>) => K(`K.note(${j(id)}, ${j(text)}, ${j(o)})`);
  const pill = (id: string, text: string, o: Record<string, unknown>) => K(`K.pill(${j(id)}, ${j(text)}, ${j(o)})`);
  const chapter = (n: string, t: string, s = '') => K(`K.chapter(${j(n)}, ${j(t)}, ${j(s)})`);
  const travel = (ids: string[], ms = 1400) => K(`K.travel(${j(ids)}, ${ms})`);
  const light = (...ids: string[]) => K(`K.light(...${j(ids)})`);
  const unlight = (...ids: string[]) => K(`K.unlight(...${j(ids)})`);
  const dim = (...ids: string[]) => K(`K.dim(...${j(ids)})`);
  const remove = (...ids: string[]) => K(`K.remove(...${j(ids)})`);
  const hide = () => K('K.hideTicket()');
  /** A distribution's state, as a pill under its card; replaced when it changes. */
  const state = async (id: string, text: string, x: number, y: number, c: string) => { await remove(`${id}:s`); await pill(`${id}:s`, text, { x, y, c }); };

  return [
    // ------------------------------------------------------------------ 0
    {
      card: ['The north-star architecture', 'How the TransactHub studio and runtime fit together, and why. No screens: pictures only.'],
      say: 'This video explains the north-star architecture of TransactHub: how the designer studio and the runtime fit together, and why they are built this way. There are no screens in this video, only pictures. Where something is not built yet, we will say so.',
    },
    {
      say: 'The whole design rests on one idea. A payment is data, not code. A scheme or a bank is never written into shared code. A scheme is a leg, and a bank is configuration: values, hooks and port bindings.',
      do: async () => {
        await chapter('1', 'The one idea', 'A payment is data, not code');
        await card('pay', { x: 90, y: 200, w: 330, icon: 'ticket', k: 'A payment', r: 'Data', d: 'One model, one store, one lifecycle.', c: 'ticket' });
        await settle();
        await card('sch', { x: 500, y: 140, w: 330, icon: 'network', k: 'A scheme', r: 'A leg', d: 'Fills a slot in a platform workflow.', c: 'scheme' });
        await card('bank', { x: 500, y: 320, w: 330, icon: 'dial', k: 'A bank', r: 'Configuration', d: 'Values, hooks and port bindings.', c: 'bank' });
        await settle();
        await card('code', { x: 910, y: 230, w: 300, icon: 'gear', k: 'Shared code', r: 'Names neither', d: 'A build check fails if it names a bank.', c: 'safety' });
        await arrow('a1', 'sch', 'code'); await arrow('a2', 'bank', 'code');
      },
    },
    // ------------------------------------------------------------------ 1
    {
      say: 'At the centre is one model: ISO 20 022, the international standard for payment messages. Every workflow works on one canonical context built on it. The database is shaped on it too.',
      do: async () => {
        await chapter('2', 'ISO 20022 at the centre', 'Scheme formats are mapped at the edges');
        await card('iso', { x: 470, y: 250, w: 340, icon: 'globe', k: 'The canonical model', r: 'ISO 20022', d: 'pain.001 and pacs.008 shapes. The workflow context.', c: 'recipe' });
      },
    },
    {
      say: 'A scheme speaks its own format. The wages protection system, WPS, sends salary files and acknowledgements in its own layout. Only map blocks touch another format: one map reads the file in, another writes the reply out.',
      do: async () => {
        await card('sif', { x: 40, y: 140, w: 280, icon: 'file', k: 'A WPS salary file', r: 'Scheme format', c: 'scheme' });
        await card('ack', { x: 40, y: 380, w: 280, icon: 'file', k: 'An acknowledgement', r: 'Scheme format', c: 'scheme' });
        await card('out', { x: 960, y: 260, w: 280, icon: 'file', k: 'A file to the scheme', r: 'Scheme format', c: 'scheme' });
        await settle();
        await arrow('m1', 'sif', 'iso', { label: 'intake map' });
        await arrow('m2', 'ack', 'iso', { label: 'map' });
        await arrow('m3', 'iso', 'out', { label: 'map' });
      },
    },
    {
      say: 'A field the standard has no place for is never left untyped. It becomes a registered extension, typed supplementary data. And the context carries ids, not people: names and accounts are read from the store by id, so the engine\'s history holds no personal data.',
      do: async () => {
        await note('n1', 'No untyped field: a scheme or bank field is a registered extension.<br>The context carries ids; parties and accounts are read from the store.', { x: 330, y: 470, w: 620 });
        await travel(['m1', 'm3'], 1300);
      },
    },
    // ------------------------------------------------------------------ 2
    {
      say: 'Next, the store. There is one store, with one model, on SQL Server or on PostgreSQL. The choice is made in one place. A build test fails if a database driver or raw SQL appears anywhere else, and every store test runs on both databases.',
      do: async () => {
        await hide();
        await chapter('3', 'One store, insert-only', 'SQL Server or PostgreSQL, the same model');
        await card('store', { x: 470, y: 130, w: 340, icon: 'db', k: 'One store', r: 'EF Core', d: 'Payments, facts, messages, releases, values.', c: 'run' });
        await card('mssql', { x: 230, y: 330, w: 280, icon: 'db', k: 'SQL Server', r: 'Provider', c: 'safety' });
        await card('pg', { x: 770, y: 330, w: 280, icon: 'db', k: 'PostgreSQL', r: 'Provider', c: 'safety' });
        await settle();
        await arrow('s1', 'store', 'mssql'); await arrow('s2', 'store', 'pg');
      },
    },
    {
      say: 'The store is insert-only. A payment\'s status is never overwritten. Each change is a new fact, and the current status is worked out from the facts. Each fact records the hash of the fact before it, so a check can name the first fact that was changed.',
      do: async () => {
        await card('f1', { x: 120, y: 480, w: 200, icon: 'log', k: 'Fact 1', r: 'Accepted', c: 'station' });
        await card('f2', { x: 400, y: 480, w: 200, icon: 'log', k: 'Fact 2', r: 'Screened', c: 'station' });
        await card('f3', { x: 680, y: 480, w: 200, icon: 'log', k: 'Fact 3', r: 'Sent', c: 'station' });
        await card('f4', { x: 960, y: 480, w: 200, icon: 'log', k: 'Fact 4', r: 'Settled', c: 'station' });
        await settle();
        await arrow('h1', 'f1', 'f2', { label: 'hash' }); await arrow('h2', 'f2', 'f3', { label: 'hash' }); await arrow('h3', 'f3', 'f4', { label: 'hash' });
      },
    },
    {
      say: 'In a deployment the database itself enforces this. The hosts sign in as a login that may read and insert, and is refused an update or a delete. On the laptop stack a test proves it, after a whole switch-over between two distributions.',
      do: async () => {
        await pill('p1', 'SELECT, INSERT: allowed', { x: 90, y: 260, c: 'bank' });
        await pill('p2', 'UPDATE, DELETE: refused by the database', { x: 860, y: 260, c: 'scheme' });
        await light('f1', 'f2', 'f3', 'f4');
      },
    },
    {
      say: 'There is no table per scheme and no table per bank. A scheme and a bank are columns. That is what lets a new scheme arrive without a change to the database.',
      do: async () => { await unlight('f1', 'f2', 'f3', 'f4'); await note('n2', 'No schema per scheme. No schema per bank. They are columns.', { x: 40, y: 110, w: 400 }); },
    },
    // ------------------------------------------------------------------ 3
    {
      say: 'Now the engines: the part that runs a workflow for days, waits for answers, and survives restarts. TransactHub has four engines behind one contract. The studio exports one expanded workflow, and one interpreter runs it on each engine.',
      do: async () => {
        await chapter('4', 'Four engines, one contract', 'Temporal is the default');
        await card('wf', { x: 470, y: 120, w: 340, icon: 'recipe', k: 'The expanded workflow', r: 'One interpreter', d: 'Behind one seam: IEngine.', c: 'recipe' });
        await card('tmp', { x: 40, y: 340, w: 280, icon: 'gear', k: 'Temporal', r: 'Default', d: 'On SQL Server, through our plugin.', c: 'run' });
        await card('dtfx', { x: 350, y: 340, w: 280, icon: 'gear', k: 'DTFx', r: 'Engine', d: 'SQL Server task hub.', c: 'safety' });
        await card('elsa', { x: 660, y: 340, w: 280, icon: 'gear', k: 'Elsa 3', r: 'Engine', d: 'Interpreter in one activity.', c: 'safety' });
        await card('flw', { x: 970, y: 340, w: 280, icon: 'gear', k: 'Flowable 7', r: 'Engine', d: 'External workers over REST.', c: 'safety' });
        await settle();
        await arrow('e1', 'wf', 'tmp'); await arrow('e2', 'wf', 'dtfx'); await arrow('e3', 'wf', 'elsa'); await arrow('e4', 'wf', 'flw');
      },
    },
    {
      say: 'One conformance kit, fifty-seven scenarios, proves they behave the same. On the twenty-third of November, DTFx and Elsa passed all fifty-seven. Each installation runs one engine. Temporal is the default, running on SQL Server through a plugin the team keeps.',
      do: async () => { await light('tmp', 'e1'); await pill('kit', 'Conformance kit: 59 scenarios, one kit for every engine', { x: 380, y: 520, c: 'test' }); },
    },
    {
      say: 'One gap to know about. On DTFx and Elsa, the long-running host runs each payment in a run of its own, and that run does not yet survive a restart of its host. Flowable deploys per payment. Temporal has neither limit. This is Issue I 165.',
      do: async () => { await note('n3', 'Not built yet: a DTFx or Elsa run does not survive a restart of its host; Flowable deploys per payment (I-165).', { x: 330, y: 570, w: 640 }); },
    },
    // ------------------------------------------------------------------ 4
    {
      say: 'How does new work reach a running bank? There are three layers, each changed by different people, in a different way.',
      do: async () => {
        await chapter('5', 'Three layers', 'Runtime software, distribution, configuration release');
        await card('L1', { x: 60, y: 130, w: 1160, icon: 'layers', k: 'Runtime software', r: 'Container images', d: 'Engine hosts, workers, store, APIs, consoles. Upgraded by the bank\'s service engineers.', c: 'safety' });
      },
    },
    {
      say: 'The second layer is the distribution: a complete, signed copy of everything one bank runs. The platform release, the scheme releases the bank licenses, the bank\'s own pack, and the engine binding, resolved together by the studio. It is the only way new workflow code arrives.',
      do: async () => {
        await card('L2', { x: 60, y: 290, w: 1160, icon: 'crate', k: 'Distribution, per bank', r: 'Signed, complete, never edited', d: 'Platform + schemes + bank pack + engine binding. Installed from GitHub in the service console.', c: 'recipe' });
      },
    },
    {
      say: 'The third layer is the configuration release: the bank\'s values. Dials within their bounds, rule values, port bindings and calendars, each dated from when it applies. It changes values only, never which code runs.',
      do: async () => {
        await card('L3', { x: 60, y: 450, w: 1160, icon: 'dial', k: 'Configuration release, per bank', r: 'Signed, effective-dated values', d: 'Dials, rule values, port bindings, calendars. No deployment.', c: 'bank' });
      },
    },
    {
      say: 'Why three? If a scheme were deployed on its own, the runtime would combine versions at run time, and every pair would need testing. If everything were one package, a fix to one scheme would redeploy every bank. And if values were inside the distribution, a limit change would need a deployment.',
      do: async () => { await light('L2'); await settle(); await unlight('L2'); await light('L3'); },
    },
    {
      say: 'So the authoring units in the studio stay small: the platform pack, each scheme pack, each bank pack, each with its own version. The deployment unit is the distribution, and the studio refuses to build one whose scheme does not fit its platform\'s slot.',
      do: async () => { await unlight('L3'); },
    },
    // ------------------------------------------------------------------ 5
    {
      say: 'A payment is pinned to a distribution at acceptance. The runtime records the active distribution\'s id on the payment, and that never changes. Every leg, activity and rule of that payment runs from it. A payment never mixes distributions.',
      do: async () => {
        await chapter('6', 'Pinned, side by side, drained', 'From one version to the next');
        await card('A', { x: 120, y: 130, w: 300, icon: 'crate', k: 'Distribution A', r: '2026.11.23-1', c: 'recipe' });
        await state('A', 'Active', 440, 165, 'var(--bank)');
        await card('p1', { x: 120, y: 330, w: 220, icon: 'ticket', k: 'Payment 1', r: 'Pinned to A', c: 'ticket' });
        await settle();
        await arrow('pa', 'p1', 'A', { label: 'pinned at acceptance' });
      },
    },
    {
      say: 'Now a second distribution, B, is installed beside it. Each has its own copy: its own workflow definitions and, on Temporal, its own worker build id. Activating B moves A to draining, in the same step.',
      do: async () => {
        await card('B', { x: 760, y: 130, w: 300, icon: 'crate', k: 'Distribution B', r: '2026.11.23-2', c: 'recipe' });
        await state('B', 'Installed', 1080, 165, 'var(--safety)');
        await settle();
        await state('B', 'Active', 1080, 165, 'var(--bank)');
        await state('A', 'Draining', 440, 165, 'var(--rule)');
      },
    },
    {
      say: 'From that instant, new payments take B. Payment 1 keeps running on A, to its end, even if an answer from the scheme arrives days later: the answer is handed to the host of A.',
      do: async () => {
        await card('p2', { x: 760, y: 330, w: 220, icon: 'ticket', k: 'Payment 2', r: 'Pinned to B', c: 'ticket' });
        await settle();
        await arrow('pb', 'p2', 'B', { label: 'pinned at acceptance' });
        await card('ans', { x: 400, y: 460, w: 260, icon: 'talk', k: 'A late answer', r: 'For payment 1', c: 'scheme' });
        await settle();
        await arrow('ap', 'ans', 'p1');
        await travel(['ap', 'pa'], 1300);
      },
    },
    {
      say: 'When no payment pinned to A is open, A is drained. It stays up for its longest declared wait, for late messages. Only then may it be removed. Removal is refused while a pinned payment is open. The package stays on GitHub and in the store, as a record.',
      do: async () => {
        await hide();
        await dim('p1', 'ans');
        await state('A', 'Drained', 440, 165, 'var(--safety)');
        await settle();
        await state('A', 'Removed', 440, 165, 'var(--muted)');
        await dim('A');
      },
    },
    {
      say: 'Rollback is just another activation: activate the earlier distribution again. If it was removed, reinstall it from GitHub first. Payments that started on the newer one finish on it. Every one of these changes is a new row, with who did it and when.',
      do: async () => { await note('n4', 'States: Installed, Active, Draining, Drained, Removed.<br>Rollback: activate the earlier one again (reinstall it first if removed).', { x: 360, y: 570, w: 620 }); },
    },
    // ------------------------------------------------------------------ 6
    {
      say: 'Inside a distribution, what does a bank actually run? Platform workflows. The corporate payment workflow validates, screens, checks limits, reserves the funds and routes the payment. It has a slot, the scheme leg, which a scheme fills.',
      do: async () => {
        await chapter('7', 'Platform workflows, slots, legs, hooks and rules', 'What a distribution carries');
        await card('corp', { x: 60, y: 120, w: 620, icon: 'recipe', k: 'Platform workflow', r: 'p.wf.corporate.v1', d: 'Validate, screen, limits, reserve, route ... slot: scheme-leg ... post.', c: 'recipe' });
      },
    },
    {
      say: 'A scheme\'s dispatch table says which message starts which workflow, and which leg fills the slot. For WPS, a salary file starts an outward corporate payment, and the WPS leg fills its scheme leg.',
      do: async () => {
        await card('disp', { x: 760, y: 120, w: 460, icon: 'desk', k: 'Dispatch table', r: 'uae.wps', d: 'A salary file starts p.wf.corporate; the WPS leg fills the slot.', c: 'scheme' });
        await card('leg', { x: 760, y: 300, w: 460, icon: 'piece', k: 'Scheme leg', r: 'uae.wps.wf.salary-file-out.v1', d: 'Builds the file, sends it, waits for the answer.', c: 'scheme' });
        await settle();
        await arrow('dl', 'disp', 'leg');
        await arrow('lc', 'leg', 'corp', { label: 'fills scheme-leg' });
      },
    },
    {
      say: 'The same leg idea works for refunds. A refund is an order of its own, on the platform\'s refund workflow. The platform finds the earlier payment, prices, approves and posts. WPS is the leg that answers and returns the funds. The refund cycle is only partly built, and its layouts are not confirmed.',
      do: async () => {
        await card('ref', { x: 60, y: 300, w: 620, icon: 'recipe', k: 'Refund order', r: 'p.wf.refund.v1', d: 'Slots: scheme-leg, scheme-refuse. WPS is the leg (partly built).', c: 'recipe' });
        await settle();
        await arrow('lr', 'leg', 'ref', { label: 'also a refund leg' });
      },
    },
    {
      say: 'A bank adds its own logic at hook points, for example before release, without touching the platform workflow. Its rules use parameters: every value a bank may change has a default and bounds, and is never a constant.',
      do: async () => {
        await card('hook', { x: 60, y: 470, w: 360, icon: 'plug', k: 'Bank hook', r: 'before-release', d: 'The bank\'s own step or rule set.', c: 'bank' });
        await card('rule', { x: 470, y: 470, w: 360, icon: 'check', k: 'Rules and parameters', r: 'Default and bounds', d: 'A value is a dial, never a constant.', c: 'rule' });
        await settle();
        await arrow('hc', 'hook', 'corp');
      },
    },
    {
      say: 'One rule ties it together. Values are looked up by date, so a new value can reach a payment already in flight, where its rule says so. Logic is pinned, so a running payment never changes the code it runs.',
      do: async () => { await pill('v1', 'Values: by date, can reach payments in flight', { x: 880, y: 490, c: 'bank' }); await pill('v2', 'Logic: pinned to the distribution', { x: 880, y: 540, c: 'recipe' }); },
    },
    // ------------------------------------------------------------------ 7
    {
      say: 'Files. Every file the runtime receives or sends goes through one service, the File API. Nothing else reads a partner\'s folder or writes to one.',
      do: async () => {
        await chapter('8', 'The File API: the only door for files', 'In, checked, kept, routed; and out');
        await card('fapi', { x: 470, y: 230, w: 340, icon: 'door', k: 'The File API', r: 'Its own service', d: 'Check, decrypt, keep with its hash, each step a fact.', c: 'door' });
      },
    },
    {
      say: 'Files come in four ways: a channel\'s upload over HTTPS, a person\'s upload on a screen, the SFTP gateway, and the inboxes it polls. On the laptop the inbox is a folder. At a bank it is the partner\'s SFTP folder, reached by the File API\'s own SFTP adapter.',
      do: async () => {
        await card('i1', { x: 40, y: 110, w: 300, icon: 'network', k: 'Channel upload', r: 'HTTPS', c: 'scheme' });
        await card('i2', { x: 40, y: 230, w: 300, icon: 'actor', k: 'A person\'s upload', r: 'Upload screen', c: 'scheme' });
        await card('i3', { x: 40, y: 350, w: 300, icon: 'lock', k: 'Partners\' SFTP', r: 'sftp adapter', c: 'scheme' });
        await card('i4', { x: 40, y: 470, w: 300, icon: 'box', k: 'Inbox folder', r: 'Laptop', c: 'scheme' });
        await settle();
        for (const i of ['i1', 'i2', 'i3', 'i4']) await arrow(`${i}f`, i, 'fapi', { side: 'h' });
      },
    },
    {
      say: 'Behind the intake is the front door. A file that starts a payment pins it to the bank\'s active distribution, reads it with the scheme\'s intake map, and starts it on the engine host. A file for a running payment, such as an acknowledgement, is matched to its payment, and handed to the host of the distribution it is pinned to.',
      do: async () => {
        await card('fd', { x: 900, y: 130, w: 330, icon: 'desk', k: 'Front door', r: 'Pin, start, correlate', c: 'recipe' });
        await card('eh', { x: 900, y: 330, w: 330, icon: 'gear', k: 'Engine host', r: 'One host per distribution', c: 'run' });
        await settle();
        await arrow('ffd', 'fapi', 'fd'); await arrow('fdeh', 'fd', 'eh');
        await travel(['i3f', 'ffd', 'fdeh'], 1200);
      },
    },
    {
      say: 'Going out is the same door in reverse. A workflow\'s file goes to the File API\'s outbox, is kept with its payment, and is delivered by the bank\'s binding: SFTP to the Central Bank or the ministry, or a folder on a laptop. A resend from a console sends the kept bytes again.',
      do: async () => {
        await hide();
        await card('ob', { x: 900, y: 500, w: 330, icon: 'file', k: 'Outbox, then delivery', r: 'sftp or directory', c: 'door' });
        await settle();
        await arrow('ehob', 'eh', 'ob'); await arrow('obf', 'ob', 'fapi');
      },
    },
    {
      say: 'Some file rules, such as allowed names and sizes, are still settings of the installation, because the scheme does not declare them yet. And the bank\'s partners\' SFTP host keys are waiting on each bank.',
      do: async () => { await note('n5', 'Open: the scheme declares no file rules yet (I-159, I-160); host keys wait on each bank (I-161).', { x: 60, y: 560, w: 1160 }); },
    },
    // ------------------------------------------------------------------ 8
    {
      say: 'Now, where things are authored, and where they run. The studio is multi-tenant. The platform vendor, the scheme authors and every bank work in it, each in their own tenant, and each bank sees only its own content.',
      do: async () => {
        await hide();
        await chapter('9', 'The studio is multi-tenant; a runtime is one bank', 'GitHub in between');
        await card('st', { x: 60, y: 140, w: 330, icon: 'blueprint', k: 'The studio', r: 'Multi-tenant', d: 'Platform, schemes, and every bank, each in its tenant.', c: 'recipe' });
      },
    },
    {
      say: 'The studio keeps no database of its own. Its only store is one GitHub branch, folder content configurations. A change in progress is a branch of its own, and a published file is never rewritten.',
      do: async () => {
        await card('gh', { x: 470, y: 140, w: 340, icon: 'branch', k: 'One GitHub branch', r: 'content-configurations/', d: 'Change sets as branches; signed distributions as tags.', c: 'safety' });
        await settle();
        await arrow('stgh', 'st', 'gh');
      },
    },
    {
      say: 'The studio builds a distribution for every bank, and signs it. Each bank has its own runtime. A runtime is told its bank when it is installed, and refuses another bank\'s distribution, configuration release, file or backup, whoever asks.',
      do: async () => {
        await card('r1', { x: 900, y: 110, w: 330, icon: 'store', k: 'Runtime of bank 1', r: 'TH_BANK', c: 'run' });
        await card('r2', { x: 900, y: 290, w: 330, icon: 'store', k: 'Runtime of bank 2', r: 'TH_BANK', c: 'run' });
        await settle();
        await arrow('g1', 'gh', 'r1', { label: 'bank 1 only' }); await arrow('g2', 'gh', 'r2', { label: 'bank 2 only' });
        await pill('x', 'Another bank\'s distribution: refused at verify, install and activate', { x: 460, y: 470, c: 'scheme' });
      },
    },
    // ------------------------------------------------------------------ 9
    {
      say: 'Last, who may change what. In the studio, every change is maker and checker. The person who made a change never approves it. A bank configurator makes the bank\'s change, and a bank approver approves it.',
      do: async () => {
        await chapter('10', 'Maker-checker and consent', 'Two people for every change');
        await card('mk', { x: 60, y: 140, w: 300, icon: 'actor', k: 'Maker', r: 'Studio', d: 'Makes a change set.', c: 'recipe' });
        await card('ck', { x: 420, y: 140, w: 300, icon: 'shield', k: 'Checker', r: 'Never the maker', d: 'Approves it as a version.', c: 'recipe' });
        await settle();
        await arrow('mc', 'mk', 'ck');
      },
    },
    {
      say: 'In the runtime, every change of the system is asked for by one person and carried out only when another person, with the bank approver\'s role, consents. Installing, activating, draining, removing, rolling back, restoring, unmasking a field, changing a register. Each request and each consent is a fact.',
      do: async () => {
        await card('se', { x: 60, y: 360, w: 300, icon: 'actor', k: 'Service engineer', r: 'Asks', d: 'With a reason.', c: 'run' });
        await card('ba', { x: 420, y: 360, w: 300, icon: 'shield', k: 'Bank approver', r: 'Consents', d: 'Another person.', c: 'bank' });
        await card('done', { x: 800, y: 360, w: 300, icon: 'gear', k: 'Carried out', r: 'A fact each step', c: 'safety' });
        await settle();
        await arrow('sb', 'se', 'ba'); await arrow('bd', 'ba', 'done');
        await travel(['sb', 'bd'], 1200);
      },
    },
    {
      say: 'An operator\'s decision on a payment, such as approving a hold, is a business decision instead. It keeps the second approver its view declares.',
      do: async () => { await hide(); await note('n6', 'A business decision on a payment (approve a hold, retry, repair) keeps the second approver its view declares.', { x: 360, y: 530, w: 620 }); },
    },
    // ------------------------------------------------------------------ 10
    {
      say: 'What is not built yet, and matters. The owner has just approved four more changes, which are being built now: bank values reaching a runtime only through signed configuration releases with a bank approver\'s consent; native code carried in each distribution, not in the runtime image; the engine host reading the bank\'s values; and ports for notifications and event streaming.',
      do: async () => {
        await chapter('11', 'What is not built yet', 'As of 23 November 2026');
        await note('t1', 'Being built: values only by signed, consented configuration releases; native code in each distribution; the engine host reading bank values; notification and event-streaming ports.', { x: 80, y: 130, w: 1100 });
      },
    },
    {
      say: 'Also open: durable DTFx and Elsa runs, the scheme\'s own file rules, and the counterparts each bank must still name, such as its core banking settings and screening engines. Other schemes, such as IPI, are deferred by the owner. WPS itself is provisional until its rulebook confirms it.',
      do: async () => {
        await note('t2', 'Open: durable DTFx and Elsa runs (I-165); file rules (I-159, I-160); each bank\'s counterparts (I-85, I-163); other schemes deferred. WPS is provisional.', { x: 80, y: 290, w: 1100 });
      },
    },
    {
      card: ['That is the north star', 'Next: part 3, one payment end to end, from the studio to the operations console.'],
      say: 'That is the north-star architecture: one model, one insert-only store, four engines behind one contract, three layers, pinned payments, one door for files, and two people for every change. In the next video, we follow one payment through all of it, from the studio to the operations console.',
    },
  ];
}
