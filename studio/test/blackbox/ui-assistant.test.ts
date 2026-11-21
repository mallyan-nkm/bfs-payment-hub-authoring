import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { toYaml } from '../../src/core/yaml.js';
import { startServer, STUDIO, type Server } from './server.js';

// Black box, in the browser: the assistant beside every surface (I-29). "What is happening" explains
// the field in focus and the document, with no model. "Ask" drives the agent; the model is replayed
// from a recorded conversation (TH_AI_REPLAY), so the test needs no credentials and is the same
// every time. The agent asks before each change; the designer allows once, allows always, denies,
// and undoes.

const require = createRequire(import.meta.url);
let server: Server;
let browser: any;
let page: any;
const problems: string[] = [];

const RULE = {
  kind: 'rule', name: 'uae.wps.rule.days-in-period.v1', release: '1.0.0', title: 'Days in the pay period are from 1 to 31', ruleKind: 'field', level: 'item', repeatability: 'non-repeatable',
  when: [{ match: { fact: 'WpsItem', as: 'i', where: 'i.daysInPeriod < 1 or i.daysInPeriod > 31' } }],
  then: [{ yield: { finding: { severity: 'reject', path: 'items[].ext.uae.wps.daysInPeriod', reasonCode: 'DT01' } } }],
  examples: [
    { name: 'passes', evaluationDate: '2026-11-17', given: { WpsItem: { daysInPeriod: 30 } }, expect: { findings: [] } },
    { name: 'fails, zero', evaluationDate: '2026-11-17', given: { WpsItem: { daysInPeriod: 0 } }, expect: { findings: [{ reasonCode: 'DT01' }] } },
    { name: 'boundary, 31 passes', evaluationDate: '2026-11-17', given: { WpsItem: { daysInPeriod: 31 } }, expect: { findings: [] } },
  ],
  provenance: { origin: 'authored', status: 'provisional', clauses: ['Synthetic training extract'] },
};
const tool = (id: string, name: string, input: any = {}) => ({ type: 'tool_use', id, name, input });
const text = (t: string) => ({ type: 'text', text: t });
const TURNS = [
  { content: [text('First I look at what is there.'), tool('t1', 'get_document'), tool('t2', 'search_catalogue', { query: 'wps', kind: 'rule' })] },
  { content: [tool('t3', 'edit_document', { yaml: toYaml(RULE), summary: 'Write the days-in-period rule with three examples' })] },
  { content: [tool('t4', 'check_document')] },
  { content: [text('**Done.** The rule `uae.wps.rule.days-in-period.v1` and its three examples pass.\n\n- Save it as a draft\n- a checker publishes it.')] },
  { content: [tool('t5', 'edit_document', { yaml: toYaml({ ...RULE, title: 'Days in the period: 1 to 31' }), summary: 'A shorter title' })] },
  { content: [text('The title is shorter now.')] },
];

beforeAll(async () => {
  if (!fs.existsSync(path.join(STUDIO, 'web', 'dist', 'index.html'))) spawnSync('npm', ['run', 'web:build'], { cwd: STUDIO });
  const replay = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'th-replay-')), 'turns.json');
  fs.writeFileSync(replay, JSON.stringify(TURNS));
  server = await startServer({ TH_AI_REPLAY: replay });
  browser = await require('playwright').chromium.launch();
  page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
  page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
}, 180000);
afterAll(async () => {
  await browser?.close();
  server?.stop();
});

const go = async (hash: string) => {
  await page.goto(`${server.url}/#/${hash}`);
  await page.waitForLoadState('networkidle');
};
const dock = () => page.getByRole('complementary', { name: 'Assistant' });

describe('the assistant beside every surface', () => {
  it('explains the field in focus and the document as it is built', async () => {
    await go('editors?kind=rule');
    await page.getByRole('button', { name: 'Start new' }).click();
    await page.getByLabel('Rule level').focus();
    await expect.poll(async () => (await dock().getByTestId('field-help').textContent()) ?? '').toMatch(/Rule level.*Where the value the rule looks at lives/);
    await expect.poll(async () => (await dock().getByTestId('explanation').textContent()) ?? '').toMatch(/New rule.*Before it can be published/);
  }, 60000);

  it('builds a rule with its examples, asking first; allow once applies it to the form', async () => {
    await dock().getByRole('tab', { name: 'Ask' }).click();
    await dock().getByLabel('Ask the assistant').fill('Build a rule that the days in the pay period are 1 to 31, with tests.');
    await dock().getByRole('button', { name: 'Send' }).click();
    const pending = dock().getByTestId('pending');
    await expect.poll(async () => (await pending.textContent()) ?? '').toMatch(/Write the days-in-period rule with three examples/);
    await expect.poll(async () => (await pending.textContent()) ?? '').toMatch(/tests: pass/);
    // Nothing changed yet.
    expect(await page.getByLabel('Name', { exact: true }).inputValue()).toBe('uae.wps.rule.new.v1');
    await expect.poll(async () => (await dock().textContent()) ?? '').toMatch(/Searched the catalogue for "wps"/);
    await pending.getByRole('button', { name: 'Allow once' }).click();
    await expect.poll(() => page.getByLabel('Name', { exact: true }).inputValue()).toBe(RULE.name);
    await expect.poll(async () => (await dock().textContent()) ?? '').toMatch(/a checker publishes it/);
    // The reply's markdown is rendered, not shown as marks (I-31).
    const reply = dock().locator('.bubble.assistant', { hasText: 'a checker publishes it' });
    expect(await reply.locator('b', { hasText: 'Done.' }).count()).toBe(1);
    expect(await reply.locator('code', { hasText: 'uae.wps.rule.days-in-period.v1' }).count()).toBe(1);
    expect(await reply.locator('li').count()).toBe(2);
    expect(await dock().locator('.ask-log').textContent()).not.toMatch(/\*\*|`/);
    await expect.poll(() => page.locator('.example.ex-pass').count()).toBe(3);
    await expect.poll(async () => (await dock().textContent()) ?? '').toMatch(/c1\s*Write the days-in-period rule/);
  }, 60000);

  it('allow always, then the designer undoes the assistant\'s change', async () => {
    await dock().getByLabel('Ask the assistant').fill('Make the title shorter.');
    await dock().getByRole('button', { name: 'Send' }).click();
    await dock().getByTestId('pending').getByRole('button', { name: 'Allow always' }).click();
    await expect.poll(() => page.getByLabel('Title', { exact: true }).inputValue()).toBe('Days in the period: 1 to 31');
    await expect.poll(async () => (await dock().textContent()) ?? '').toMatch(/Allowed always here: edit_document/);
    await dock().locator('.dock-changes li', { hasText: 'A shorter title' }).getByRole('button', { name: 'Undo' }).click();
    await expect.poll(() => page.getByLabel('Title', { exact: true }).inputValue()).toBe(RULE.title);
    expect(await page.getByLabel('Name', { exact: true }).inputValue()).toBe(RULE.name);
  }, 60000);

  it('is on the designer, a bank\'s configuration and the test bench too', async () => {
    await go('designer');
    await page.getByLabel('Published workflow').selectOption('uae.wps.wf.salary-file-out.v1');
    await page.getByRole('button', { name: 'Open', exact: true }).click();
    await dock().getByRole('tab', { name: 'What is happening' }).click();
    await expect.poll(async () => (await dock().getByTestId('explanation').textContent()) ?? '').toMatch(/fills the scheme-leg slot.*file-checks/);

    await go('tenant');
    await page.getByLabel('Tenant', { exact: true }).selectOption('example-bank');
    await page.waitForTimeout(500);
    await go('tenant');
    await expect.poll(async () => (await dock().getByTestId('explanation').textContent()) ?? '', { timeout: 15000 }).toMatch(/configuration.*passes every check/);
    await page.getByLabel('Tenant', { exact: true }).selectOption('owner');

    await go('tests');
    await page.getByRole('button', { name: 'Ad hoc bench' }).click();
    await page.getByLabel('Target', { exact: true }).selectOption('uae.p.validate.iban.v1');
    await expect.poll(async () => (await dock().getByTestId('explanation').textContent()) ?? '').toMatch(/The bench will run uae\.p\.validate\.iban\.v1/);
    await page.getByLabel('Run tenant').focus();
    await expect.poll(async () => (await dock().getByTestId('field-help').textContent()) ?? '').toMatch(/Run tenant/);
  }, 60000);

  it('closes and opens again, and nothing failed on the way', async () => {
    await dock().getByRole('button', { name: 'Close the assistant' }).click();
    await page.getByRole('button', { name: 'Open the assistant' }).click();
    await expect.poll(async () => await dock().count()).toBe(1);
    expect(problems).toEqual([]);
  });
});
