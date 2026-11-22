import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { toYaml } from '../../src/core/yaml.js';
import { ACK_IN, FILE_IN, FILE_IN_SCRIPT, FILE_OUT, GARBAGE, LEG, PORT, RULE, RULESET, SCHEME, SEND, SIM, SUBFLOW, SUBMIT, USD, VALID, VALIDATE } from './demo-scheme.js';
import { client, signInPage, startServer, STUDIO, type Server } from './server.js';

// Black box, in the browser: the walk-through of the user guide's "Build a full workflow", done only
// in the web app. A shared designer builds the synthetic scheme `uae.demo` and its leg; a platform
// approver approves each change set as a version; the bank configurator enables the rail and the
// bank approver approves it; a file is paid end to end; the export is downloaded and
// deployed on DTFx with a separate worker per queue. Every click here is a step of the guide.

const require = createRequire(import.meta.url);
let server: Server;
let browser: any;
let page: any;
const problems: string[] = [];
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-ui-build-'));

beforeAll(async () => {
  if (!fs.existsSync(path.join(STUDIO, 'web', 'dist', 'index.html'))) spawnSync('npm', ['run', 'web:build'], { cwd: STUDIO });
  server = await startServer();
  browser = await require('playwright').chromium.launch();
  page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true });
  page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
  page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
  await signInPage(page, server.url, 'shared.designer');
}, 180000);
afterAll(async () => {
  await browser?.close();
  server?.stop();
});

const go = async (hash: string) => {
  await page.goto(`${server.url}/#/${hash}`);
  await page.waitForLoadState('networkidle');
};
/** Sign in as another user: the studio takes the user from the session (R-11-32). */
let signedIn = 'shared.designer';
const as = async (_tenant: string, actor: string) => {
  if (signedIn === actor) return;
  await signInPage(page, server.url, actor);
  signedIn = actor;
};
const status = () => page.locator('[role="status"]').last();

/** Artefact editors: a new document of a kind, written in the YAML pane, saved as a draft. */
async function editorDraft(kind: string, doc: any) {
  await go(`editors?kind=${kind}`);
  await page.getByRole('button', { name: 'Start new' }).click();
  await page.getByRole('button', { name: 'Edit the YAML' }).first().click();
  await page.getByLabel('Document YAML').first().fill(toYaml(doc));
  await page.getByRole('button', { name: 'Done' }).first().click();
  await page.getByRole('button', { name: /^Save draft$/ }).click();
  await expect.poll(async () => (await status().textContent()) ?? '').toContain('Saved draft');
}

/**
 * The maker requests the release from the screen the draft was saved on; a platform approver, who
 * is not the maker, signs in and approves the change set as a version in Governance (R-11-31).
 */
async function publishHere(expectMembers: string[], maker = 'shared.designer', approver = 'shared.checker') {
  for (const m of expectMembers.slice(1)) await expect.poll(async () => (await page.locator('main').textContent()) ?? '').toContain(m);
  await page.getByRole('button', { name: 'Request release' }).first().click();
  await expect.poll(async () => (await page.locator('[role="status"]').allTextContents()).join(' | '), { timeout: 15000 }).toContain('Release requested');
  await as('', approver);
  await go('governance');
  const row = page.locator('tr', { hasText: expectMembers[0] }).first();
  await row.getByRole('button', { name: 'Approve as version' }).click();
  // Wait for the approval to answer, either way, then say what it said.
  const all = async () => (await page.locator('[role="status"]').allTextContents()).join(' || ');
  await expect.poll(async () => /Change set CS-\S+ approved as a version|Refused/.test(await all()), { timeout: 30000 }).toBe(true);
  const text = (await page.locator('[role="status"]').allTextContents()).find((t: string) => /Change set CS-\S+ approved as a version/.test(t)) ?? '';
  expect(text, await all()).toMatch(/Change set CS-\S+ approved as a version/);
  for (const m of expectMembers) expect(text).toContain(m);
  await as('', maker);
}

/** Workflow designer: a new workflow of a kind, written in the YAML editor beside the canvas, saved. */
async function designerDraft(wfKind: string, doc: any) {
  await go('designer');
  await page.getByLabel('Start from').selectOption({ label: 'Blank' });
  await page.getByLabel('Kind', { exact: true }).selectOption('workflow');
  await page.getByLabel('Workflow kind').selectOption(wfKind);
  await page.getByLabel('Name', { exact: true }).fill(doc.name);
  await page.getByLabel('Title', { exact: true }).fill(doc.title);
  await page.getByRole('button', { name: 'Create' }).click();
  // The blank starter loads from the server; paste only once it is in, or it overwrites the paste.
  await expect.poll(() => page.getByLabel('Workflow YAML').inputValue()).toContain(doc.name);
  await page.getByLabel('Workflow YAML').fill(toYaml(doc));
  await page.waitForTimeout(800);
  await page.getByRole('button', { name: /^Save draft$/ }).click();
  await expect.poll(async () => (await status().textContent()) ?? '').toContain('Saved draft');
}

/** Test bench, ad hoc: bind ports, pick a sample and optionally a start message, run. */
async function benchRun(target: string, runTenant: string, ports: [string, string, string][], sample: string, message?: string) {
  await go('tests');
  await page.getByRole('button', { name: 'Ad hoc bench' }).click();
  await page.getByLabel('Target', { exact: true }).selectOption(target);
  await page.getByLabel('Run tenant').selectOption(runTenant);
  for (const [port, sim, scenario] of ports) {
    await page.getByRole('button', { name: 'Add a port' }).click();
    await page.getByLabel('Port', { exact: true }).last().selectOption(port);
    await page.getByLabel('Simulator', { exact: true }).last().selectOption(sim);
    await page.getByLabel('Scenario', { exact: true }).last().selectOption(scenario);
  }
  await page.getByRole('button', { name: 'Inbound sample' }).click();
  await page.getByLabel('Inbound sample').selectOption(sample);
  if (message) await page.getByLabel('Start message').selectOption(message);
  await page.getByRole('button', { name: 'Run the bench' }).click();
  await expect.poll(async () => (await page.locator('main').textContent()) ?? '', { timeout: 60000 }).toMatch(/end:(settled|rejected|pending)/);
  return (await page.locator('main').textContent()) ?? '';
}

const PLATFORM_PORTS: [string, string, string][] = [
  ['p.port.core-banking.v1', 'p.sim.core-banking.v1', 'accept'],
  ['p.port.sanctions.v1', 'p.sim.sanctions.v1', 'accept'],
  ['p.port.fraud.v1', 'p.sim.fraud.v1', 'accept'],
];
const DEMO_PORTS: [string, string, string][] = [
  [PORT.name, SIM.name, 'accept'],
  ['uae.p.port.calendar.v1', 'uae.p.sim.calendar.v1', 'accept'],
];

describe('the guide: build a full workflow in the web app', () => {
  it('step 1: saves the synthetic sample files from the test bench', async () => {
    await go('tests');
    await as('owner', 'shared.designer');
    await page.getByRole('button', { name: 'Ad hoc bench' }).click();
    await page.getByRole('button', { name: 'Inbound sample' }).click();
    for (const [name, content] of [['demo-credit-file-valid.csv', VALID], ['demo-credit-file-usd.csv', USD], ['demo-credit-file-garbage.csv', GARBAGE]]) {
      await page.getByRole('button', { name: 'Add a sample file' }).click();
      await page.getByLabel('Sample file name').fill(name);
      await page.getByLabel('Sample content').fill(content);
      await page.getByRole('button', { name: /^Save the sample as / }).click();
      await expect.poll(async () => page.getByLabel('Inbound sample').inputValue()).toBe(`samples/${name}`);
    }
  }, 60000);

  it('step 2: a port and its simulator, published together as one change set', async () => {
    await editorDraft('port', PORT);
    await editorDraft('simulator', SIM);
    await publishHere([SIM.name, PORT.name]);
  }, 60000);

  it('step 3: the rule, its rule set and the file checks', async () => {
    await editorDraft('rule', RULE);
    await publishHere([RULE.name]);
    await editorDraft('ruleset', RULESET);
    await publishHere([RULESET.name]);
    await editorDraft('activity', VALIDATE);
    await publishHere([VALIDATE.name]);
  }, 90000);

  it('step 4: the intake map with its script-behind', async () => {
    await editorDraft('map', FILE_IN);
    await page.getByLabel('Script source').fill(FILE_IN_SCRIPT);
    await page.getByRole('button', { name: /^Save the script as / }).click();
    await expect.poll(async () => (await page.locator('[role="status"]').allTextContents()).join(' | '), { timeout: 15000 }).toContain('Saved uae.demo.map.file-in.v1.ts');
    await publishHere([FILE_IN.name]);
  }, 60000);

  it('step 5: the render and acknowledgement maps and the send', async () => {
    await editorDraft('map', FILE_OUT);
    await publishHere([FILE_OUT.name]);
    await editorDraft('map', ACK_IN);
    await publishHere([ACK_IN.name]);
    await editorDraft('activity', SEND);
    await publishHere([SEND.name]);
  }, 90000);

  it('step 6: the composite, its subflow and the leg in the designer, and the scheme: one change set', async () => {
    await editorDraft('composite', SUBMIT);
    await designerDraft('subflow', SUBFLOW);
    await designerDraft('leg', LEG);
    await editorDraft('scheme', SCHEME);
    await expect.poll(async () => (await page.locator('main').textContent()) ?? '').toContain('start: payment outward corporate');
    await publishHere([SUBMIT.name, SUBFLOW.name, LEG.name, 'scheme:uae.demo']);
  }, 120000);

  it('step 7: the leg runs from the dispatch table on the bench', async () => {
    const text = await benchRun(LEG.name, 'owner', DEMO_PORTS, 'samples/demo-credit-file-valid.csv');
    expect(text).toContain('end:settled');
    expect(text).toContain('submit:accepted');
  }, 90000);

  it('step 8: as a bank, accept the scheme and enable its rail in a new configuration release', async () => {
    await as('example-bank', 'example.maker');
    await go('tenant');
    const cs = (await client(server.url, { as: 'example.maker' }).get('/api/tenants/example-bank/configuration')).data;
    const next = JSON.parse(JSON.stringify(cs.configurationSet));
    next.release = '1.1.0';
    next.accepts.push({ scope: 'uae.demo', release: '1.0.0' });
    next.values.find((v: any) => v.parameter === 'p.table.rail-choice.v1#enabledRails').value.push('uae.demo');
    await page.getByLabel('Configuration set YAML').fill(toYaml(next));
    await page.getByRole('button', { name: 'Save the configuration set' }).click();
    await expect.poll(async () => (await page.locator('main').textContent()) ?? '').toMatch(/Saved \S*config:example-bank@1\.1\.0\S* as example\.maker, as a draft/);
    // The bank approver, not the configurator, makes it the version.
    await as('example-bank', 'example.checker');
    await go('governance');
    await page.locator('tr', { hasText: 'config:example-bank' }).first().getByRole('button', { name: 'Approve as version' }).click();
    await expect.poll(async () => (await page.locator('[role="status"]').allTextContents()).join(' ')).toMatch(/approved as a version by example\.checker/);
    await as('example-bank', 'example.maker');
  }, 90000);

  it('step 9: a credit file from the bank\'s channel is paid end to end', async () => {
    const text = await benchRun('p.wf.corporate.v1', 'example-bank', [...PLATFORM_PORTS, ...DEMO_PORTS], 'samples/demo-credit-file-valid.csv', 'uae.demo.credit-file');
    expect(text).toContain('route:decided');
    expect(text).toContain('leg:settled');
    expect(text).toContain('end:settled');
  }, 90000);

  let zip = '';
  it('step 10: export for the bank with simulators, and download the zip', async () => {
    // The bank exports its own package: its configuration is its own (R-06-01).
    await go('export?workflows=p.wf.corporate.v1&target=dtfx');
    await page.getByLabel('Export tenant').selectOption('example-bank');
    await page.getByLabel('With simulators').check();
    await page.getByRole('button', { name: 'Preview' }).click();
    await expect.poll(async () => (await page.locator('main').textContent()) ?? '').toContain('th-uae-demo');
    const [download] = await Promise.all([page.waitForEvent('download'), page.getByText('Download the zip').click()]);
    zip = path.join(work, 'corporate.zip');
    await download.saveAs(zip);
    expect(fs.statSync(zip).size).toBeGreaterThan(10000);
  }, 60000);

  it('step 11: deploy on DTFx with a separate worker per queue, and run the payment', () => {
    const sample = server.contentFile('samples/demo-credit-file-valid.csv');
    const bench = [...PLATFORM_PORTS, ...DEMO_PORTS].flatMap(([p, s, sc]) => ['--bench', `${p}=${s}:${sc}`]);
    const r = spawnSync(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), ['src/cli.ts', 'deploy', zip, '--workflow', 'p.wf.corporate.v1', '--message', 'uae.demo.credit-file', '--inbound', sample, '--tenant', 'example-bank', ...bench], { cwd: STUDIO, encoding: 'utf8', env: { ...process.env }, timeout: 300000 });
    const out = (r.stdout ?? '') + (r.stderr ?? '');
    expect(out, out.slice(-1500)).toMatch(/^outcome settled/m);
    expect(out).toContain('worker th-uae-demo');
    expect(out).toContain('configuration example-bank@1.1.0 from the package');
  }, 300000);

  it('caused no script error and no server error along the way', () => {
    expect(problems).toEqual([]);
  });
});
