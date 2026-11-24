import { SLOW, type Director, type Scene } from './record.js';
import { URLS, compose, runtimeGo, runtimeSignIn, sh, stackUp, useStackStudio } from './stack.js';

// Runtime video 1: installing the laptop stack (deploy/laptop), signing in, and a tour of every screen of every console: the
// studio's surfaces by role, the service console, the operations console and the test bench. It runs on a stack brought up
// beforehand with a compose project of its own and the secrets in the environment (deploy/laptop/README.md); the commands shown
// in the terminal panel are run for real while recording, and every secret value in their output is replaced with stars.

export const NAME = 'runtime-1-install-and-screens';
export const TITLE = 'TransactHub runtime, part 1: install the laptop stack, sign in, and every screen';
export const PACE = SLOW;

export async function open(d: Director) {
  if (!(await stackUp())) throw new Error('The laptop stack does not answer: bring it up first (deploy/laptop/README.md)');
  useStackStudio(d);
  // The recorder signed in to its own studio first; cookies are kept per host, not per port, so start clean.
  await d.page.context().clearCookies();
  await d.page.goto(`${URLS.studio}/`);
  await d.page.waitForLoadState('networkidle');
}

export function scenes(d: Director): Scene[] {
  const p = () => d.page;
  const main = () => p().locator('main');
  const term = async (command: string, output: string, ms = 0) => { await d.terminal(command, output); await d.wait(ms); };
  const close = () => d.closeTerminal();
  /** The studio page for a role: sign in as the user, open the surface, point at the side bar. */
  const studio = async (user: string, hash: string) => { await d.as('', user); await d.go(hash); };
  const nav = async () => { await d.point(p().locator('nav.sidebar'), 2200); };
  const side = (name: string) => p().locator('nav.side a', { hasText: name });
  const tab = (name: string) => p().getByRole('tab', { name, exact: true });
  const svc = (page: string) => runtimeGo(d, URLS.service, `service/${page}`);
  const ops = (page: string) => runtimeGo(d, URLS.ops, page);
  const firstPayment = async () => {
    const row = p().locator('[data-testid="payment-row"]').first();
    if (await row.count()) await d.click(row.locator('a').first());
  };
  const detailTab = async (name: RegExp) => {
    const t = p().getByRole('tab', { name });
    if (await t.count()) await d.click(t.first());
  };

  return [
    // ------------------------------------------------------------------ install
    {
      card: ['Part 1: install, sign in, and every screen', 'The TransactHub studio and one bank\'s runtime, on a laptop, in Docker.'],
      say: 'In this video we install TransactHub on a laptop, sign in, and visit every screen of every console: the designer studio, the service console, the operations console, and the test bench. Everything here is synthetic: no real customer, no real account.',
    },
    {
      card: ['1. Install the laptop stack', 'Build the images, set the secrets, start the services'],
      say: 'The laptop stack runs the studio and one bank\'s runtime as separate services, in Docker containers. One runtime serves one bank. The studio serves every bank.',
    },
    {
      say: 'First, build the three images: the studio, the runtime, and Temporal on SQL Server. The build script runs from the repository root. With no options it builds from source inside Docker. With dash dash local, it uses this machine\'s dot NET, Node and Go, which is what we did here. These are its last lines.',
      do: async () => { await term('deploy/laptop/build.sh --local', `...\n${sh("docker image ls --format '{{.Repository}}:{{.Tag}} {{.Size}}' | grep '^th-laptop-'")}`); },
    },
    {
      say: 'Next, the secrets. Each person sets their own passwords and client secrets in their shell, never in a file in the repository, and no image holds one. Here are the names this stack needs. We show the names only, never the values.',
      do: async () => { await term("env | grep -o '^TH_LAPTOP_[A-Z_]*' | sort", sh("env | grep -o '^TH_LAPTOP_[A-Z_]*' | sort")); },
    },
    {
      say: 'Four are database passwords: the administrator, the runtime\'s login, the test bench\'s and Temporal\'s. One is the password of the synthetic users. Three are for machines: the release pipeline, the front door, and the engine host\'s token. Keys are not set here: a one-off container makes them into a volume on first start.',
      do: async () => { await d.wait(400); },
    },
    {
      say: 'Now check the compose file, and start the stack. Up, dash d, dash dash wait, returns when every service is healthy. The first start takes a few minutes: SQL Server starts, the migrations run once as the database owner, and the runtime\'s login is granted read and insert only.',
      do: async () => {
        await term('docker compose -f deploy/laptop/compose.yaml config -q && echo valid', compose('config -q && echo valid'));
        await d.wait(2500);
        await term('docker compose -f deploy/laptop/compose.yaml up -d --wait', compose('up -d --wait --wait-timeout 300 2>&1 | tail -18'));
      },
    },
    {
      say: 'Here is what runs. The studio on port 4317. The bank\'s development sign-in on 8480. The release API, the service console, the operations console, the test bench and the File API on 8481 to 8485. Behind them, the engine host, the native host, Temporal and SQL Server. Every port is on this machine only.',
      do: async () => { await term('docker compose -f deploy/laptop/compose.yaml ps', compose("ps --format 'table {{.Service}}\\t{{.Status}}\\t{{.Ports}}'")); },
    },
    // ------------------------------------------------------------------ studio
    {
      card: ['2. The studio', 'Sign in, and the surfaces each role sees'],
      say: 'Let us open the studio. On a laptop it uses its own development sign-in, with synthetic users. At a bank, the sign-in is the bank\'s identity provider.',
      do: async () => {
        await close();
        await p().goto(`${URLS.studio}/`);
        await p().waitForLoadState('networkidle');
        await d.click(p().getByRole('link', { name: 'Sign in' }).first());
        await p().waitForLoadState('networkidle');
        await d.wait(1500);
      },
    },
    {
      say: 'Each user has roles, and the roles decide which surfaces the studio shows. We sign in as a workflow designer, who builds shared content: the platform and the schemes.',
      do: async () => {
        await d.click(p().getByRole('button', { name: 'shared.designer', exact: true }));
        await p().getByLabel('Signed in as').waitFor();
        d.user = 'shared.designer';
        await d.wait(800);
        await nav();
      },
    },
    {
      say: 'The workflow designer: the YAML on the left and the canvas on the right edit the same workflow. The artefact editors hold every other kind: activities, rules, rule sets, tables, maps, ports and simulators.',
      do: async () => { await d.go('designer'); await d.wait(1200); await d.go('editors'); },
    },
    {
      say: 'The catalogue lists every published artefact by its name, its scope and its version. ISO data holds the canonical model, the external code sets, and each scheme\'s dispatch table.',
      do: async () => { await d.go('catalogue'); await d.wait(1200); await d.go('data?tab=model'); },
    },
    {
      say: 'The AI assistant helps beside the editors, behind the same checks as a person. Import reads an extraction bundle from an older system. The test bench runs any block or workflow against simulators. And the conformance kit runs the scenarios every engine must pass.',
      do: async () => { await d.go('assistant'); await d.wait(900); await d.go('tests'); await d.wait(900); await d.go('kit'); },
    },
    {
      say: 'Export shows the content branch: the one GitHub branch that is the studio\'s only store. Governance holds the change sets waiting for approval, the versions, and the audit log.',
      do: async () => { await d.as('', 'shared.designer'); await d.go('export'); await d.scroll(400); await d.go('governance'); },
    },
    {
      say: 'A bank\'s configurator sees something else. The bank workspace is where a bank sets its dials, binds its hooks, and enables the schemes it uses. The tenant page shows the bank\'s users and its own content.',
      do: async () => { await studio('example.maker', 'bank'); await nav(); await d.scroll(400); await d.go('tenant'); },
    },
    {
      say: 'The bank approver approves what the configurator made. A checker is never the maker. The approver lands on governance, where the bank\'s change sets wait.',
      do: async () => { await studio('example.checker', 'governance'); await nav(); },
    },
    {
      say: 'Other roles see less. A platform approver approves shared content. An auditor reads the catalogue, governance and the kit. An administrator manages the studio\'s users, and another administrator approves each change of users.',
      do: async () => { await studio('shared.auditor', 'catalogue'); await nav(); await studio('users.admin', 'governance'); await nav(); },
    },
    // ------------------------------------------------------------------ runtime sign-in
    {
      card: ['3. The runtime\'s consoles', 'Signed in at the bank\'s identity provider'],
      say: 'Now the runtime. Its web app has three consoles: the service console, the operations console, and the test bench. They share one sign-in at the bank\'s identity provider. On a laptop, that is a development provider with five synthetic users.',
      do: async () => {
        await p().goto(`${URLS.service}/`);
        await p().waitForLoadState('load');
        await d.click(p().getByRole('button', { name: 'Sign in' }));
        await p().waitForURL((u: URL) => u.port === '8480');
        await d.wait(1500);
      },
    },
    {
      say: 'A service engineer, a bank approver, an operator, a supervisor and a tester. The runtime keeps no users of its own. A role map turns the provider\'s groups into the runtime\'s roles. We sign in as the service engineer.',
      do: async () => {
        await d.point(p().locator('body'), 1600);
        await d.click(p().getByRole('button', { name: /Sign in as Example service engineer/ }));
        await p().waitForURL((u: URL) => u.port === '8482' && !u.searchParams.has('code'));
        await p().getByRole('button', { name: 'Sign out' }).waitFor();
      },
    },
    // ------------------------------------------------------------------ service console
    {
      card: ['4. The service console', 'For the bank\'s service engineers'],
      say: 'The service console is for the bank\'s service engineers. Health shows each component: the store, the read model, the package source, the signing keys, the engine, the disk, and each port the bank has bound, with a short history.',
      do: async () => { await svc('health'); await d.point(p().getByRole('table', { name: 'Health' }), 2500); },
    },
    {
      say: 'On a laptop some rows are red, and that is expected. The bank\'s core banking and screening hosts are placeholders until the bank names them. Each failing row links to its runbook.',
      do: async () => { await d.scroll(300); const link = p().locator('[data-testid="error-link"]').first(); if (await link.count()) await d.hover(link, 1500); },
    },
    {
      say: 'Content lists the signed versions offered on GitHub: the bank\'s distributions and its configuration releases. Each can be verified, compared with the active one, and installed. Below are the installed versions, their state, and how many open payments each one holds.',
      do: async () => { await svc('content'); await d.point(p().getByRole('table', { name: 'Versions' }), 1800); await d.point(p().getByRole('table', { name: 'Releases' }), 1800); },
    },
    {
      say: 'Installing, activating, draining, rolling back and removing each ask for a reason, and each waits for a bank approver, another person, to consent. We will do all of that in part 3.',
      do: async () => { const b = p().getByRole('button', { name: 'Difference' }).first(); if (await b.count()) await d.click(b); },
    },
    {
      say: 'Logs is the error and event log, filtered by code, leg, scheme, payment or trace. Errors and runbooks is the error dictionary. Each code has its likely causes, its checks, its safe actions and who to escalate to.',
      do: async () => {
        await svc('logs');
        await d.wait(1200);
        await svc('errors');
        const row = p().getByRole('table', { name: 'Errors' }).locator('tbody tr a').first();
        if (await row.count()) await d.click(row);
      },
    },
    {
      say: 'Actions holds the troubleshooting actions: retry a step, requeue a dead letter, resend a file, re-verify a package, and more. Each is recorded with its reason. Those that change the system wait for the bank approver.',
      do: async () => { await svc('actions'); await d.point(p().getByRole('table', { name: 'Catalogue' }), 2000); },
    },
    {
      say: 'Backup makes a signed backup of the bank\'s configuration, and restores one with consent. The assistant answers a service engineer\'s questions from the runbooks. It is off unless the bank switches it on, and it never changes anything.',
      do: async () => { await svc('backup'); await d.wait(1200); await svc('assistant'); },
    },
    // ------------------------------------------------------------------ operations console
    {
      card: ['5. The operations console', 'For the bank\'s operators and supervisors'],
      say: 'The operations console is where the bank\'s operators see and act on running payments. We sign in as a supervisor. The badge at the top says whether the read model has caught up with the store.',
      do: async () => { await runtimeSignIn(d, URLS.ops, 'supervisor', 'payments'); await d.point(p().locator('[data-testid="lag"]'), 1500); },
    },
    {
      say: 'Payments lists every payment and bulk file, with filters: scheme, leg, status, hold, dates, amounts, and the fields each scheme\'s view declares. A filter can be saved. The console has no code for any scheme: each scheme declares its view.',
      do: async () => { await d.point(p().getByRole('form', { name: 'Filters' }).or(p().locator('form.filters')).first(), 2200); },
    },
    {
      say: 'A payment\'s page shows its fields, its legs, its lines, its timeline of facts, the files kept with it, and its holds and actions. Personal fields are masked. To see one, an operator asks with a reason, and a bank approver must consent.',
      do: async () => {
        await firstPayment();
        await d.wait(1200);
        await detailTab(/^Legs/); await detailTab(/^Timeline/); await d.scroll(300);
        await detailTab(/^Documents/); await detailTab(/^Holds and actions/);
      },
    },
    {
      say: 'The work queue holds what waits for a person: holds, parked steps and repairs. Items can be taken, given to someone, escalated and released, one by one or in bulk. Approvals lists the business decisions that wait for a second approver.',
      do: async () => { await ops('work'); await d.wait(1500); await ops('approvals'); },
    },
    {
      say: 'Reports gives totals by scheme and status, a summary to group as you like, and the fixed reports each view declares, as CSV. Every download is recorded.',
      do: async () => { await ops('reports'); await d.scroll(400); },
    },
    {
      say: 'Breaks is the reconciliation queue: where a scheme\'s report file disagrees with the store. A resolution that changes the system needs the bank approver\'s consent. Turnaround lists the payments that took longer than their view allows.',
      do: async () => { await ops('breaks'); await d.wait(1500); await ops('turnaround'); },
    },
    {
      say: 'Retention lists what is due for erasure, and the legal holds that stop it. Consents is where a bank approver sees the changes others asked for: an unmask, an erasure, the release of a legal hold, a rebuild of the read model.',
      do: async () => { await ops('retention'); await d.wait(1500); await ops('consents'); },
    },
    {
      say: 'Registers hold the bank\'s customer data, starting with the corporate registration. It never goes to the studio or to GitHub. A register maker proposes a change, and a bank approver consents. On this laptop the register is empty, and none of the five users is a register maker, so no change can be proposed here.',
      do: async () => { await ops('registers'); },
    },
    {
      say: 'The audit trail of the console\'s own actions has no screen yet. An operator reads it from the console\'s API. A payment\'s own audit is its timeline, which we saw. We have recorded the missing screen as an issue.',
      do: async () => {
        await term('curl -s -H "Authorization: Bearer $TOKEN" http://localhost:8483/ops/api/audit | jq .', await auditSample());
        await d.wait(7000);
        await close();
      },
    },
    // ------------------------------------------------------------------ test bench
    {
      card: ['6. The test bench', 'For the bank\'s testers'],
      say: 'Last, the runtime\'s test bench, for the bank\'s testers. It runs a payment on the real engines, against the interpreter as a reference, on SQL Server or PostgreSQL. We sign in as the tester.',
      do: async () => { await runtimeSignIn(d, URLS.testbench, 'tester', 'testbench'); },
    },
    {
      say: 'Run: choose a profile, the engines, the database, the bank and the simulators, then the data. Temporal is the default engine. DTFx, Elsa and Flowable can be started for a run.',
      do: async () => { await d.scroll(350); await d.scroll(350); },
    },
    {
      say: 'Test data is the library of synthetic sample files, each with what it is expected to do. Suite holds the regression cases, and history the runs that have been made.',
      do: async () => { await d.click(tab('Test data')); await d.wait(1200); await d.click(tab('Suite')); await d.wait(1200); await d.click(tab('History')); },
    },
    {
      card: ['That is every screen', 'Next: part 2, the architecture behind them; part 3, one payment end to end.'],
      say: 'That is every screen of the laptop stack. In part 2 we look at the architecture behind them. In part 3 we follow one payment end to end, from the studio to the operations console.',
    },
  ];

  /** The console's own audit, as an operator reads it: the API's real answer, shortened. */
  async function auditSample(): Promise<string> {
    try {
      const { call } = await import('./stack.js');
      const r = await call(`${URLS.ops}/ops/api`, 'ops.supervisor', 'GET', '/audit');
      return JSON.stringify(r.data, null, 2).slice(0, 2400);
    } catch (e: any) {
      return String(e.message ?? e);
    }
  }
}
