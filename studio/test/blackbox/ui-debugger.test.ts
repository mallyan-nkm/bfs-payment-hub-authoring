import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { startServer, STUDIO, type Server } from './server.js';

// Black box, in the browser: the workflow debugger (F-17; I-32). A whole payment, a WPS salary file
// from the bank's channel, is debugged on the bench: step into and back, a breakpoint and continue,
// into a bank's hook and out again, with what each step decided and the data it changed.

const require = createRequire(import.meta.url);
let server: Server;
let browser: any;
let page: any;
const problems: string[] = [];

beforeAll(async () => {
  if (!fs.existsSync(path.join(STUDIO, 'web', 'dist', 'index.html'))) spawnSync('npm', ['run', 'web:build'], { cwd: STUDIO });
  server = await startServer();
  browser = await require('playwright').chromium.launch();
  page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.addInitScript(() => localStorage.setItem('th.dock', '0'));
  page.on('pageerror', (e: Error) => problems.push(`page error: ${e.message}`));
  page.on('response', (r: any) => r.status() >= 500 && problems.push(`${r.status()} ${r.url()}`));
}, 180000);
afterAll(async () => {
  await browser?.close();
  server?.stop();
});

const bar = () => page.getByRole('toolbar', { name: 'Debugger controls' });
const what = async () => (await page.getByTestId('dbg-now').textContent()) ?? '';
const pos = async () => Number(/Event (\d+)/.exec((await page.locator('.dbg-pos').textContent()) ?? '')?.[1]);

describe('the workflow debugger', () => {
  it('debugs a whole payment from the bench', async () => {
    await page.goto(`${server.url}/#/tests`);
    await page.waitForLoadState('networkidle');
    await page.getByLabel('Tenant', { exact: true }).selectOption('example-bank');
    await page.waitForTimeout(400);
    await page.getByRole('button', { name: 'Ad hoc bench' }).click();
    await page.getByLabel('Target', { exact: true }).selectOption('p.wf.corporate.v1');
    for (const [port, sim] of [['p.port.core-banking.v1', 'p.sim.core-banking.v1'], ['p.port.sanctions.v1', 'p.sim.sanctions.v1'], ['p.port.fraud.v1', 'p.sim.fraud.v1'], ['uae.wps.port.wps.v1', 'uae.wps.sim.wps-outward.v1'], ['uae.p.port.calendar.v1', 'uae.p.sim.calendar.v1']]) {
      await page.getByRole('button', { name: 'Add a port' }).click();
      await page.getByLabel('Port', { exact: true }).last().selectOption(port);
      await page.getByLabel('Simulator', { exact: true }).last().selectOption(sim);
      await page.getByLabel('Scenario', { exact: true }).last().selectOption('accept');
    }
    await page.getByRole('button', { name: 'Inbound sample' }).click();
    await page.getByLabel('Inbound sample').selectOption('samples/wps-sif-two-lines.sif');
    await page.getByLabel('Start message').selectOption('uae.wps.sif');
    await page.getByRole('button', { name: 'Debug step by step' }).click();
    await bar().waitFor();
    expect(await pos()).toBe(1);
  }, 60000);

  it('steps into and back, one event at a time', async () => {
    await bar().getByRole('button', { name: 'Step into ▶' }).click();
    await bar().getByRole('button', { name: 'Step into ▶' }).click();
    expect(await pos()).toBe(3);
    const third = await what();
    await bar().getByRole('button', { name: 'Step back' }).click();
    expect(await pos()).toBe(2);
    await bar().getByRole('button', { name: 'Step into ▶' }).click();
    expect(await what()).toBe(third);
  });

  it('a segment shows its own steps, and a breakpoint stops Continue there, with the decision and the data it wrote', async () => {
    // check-and-screen is a segment the corporate workflow uses: its steps are in the outline.
    await bar().getByRole('button', { name: 'Restart' }).click();
    for (let i = 0; i < 8 && !/p\.wf\.corporate\.v1/.test((await page.locator('.dbg-chain').textContent()) ?? ''); i++) await bar().getByRole('button', { name: 'Step into ▶' }).click();
    await expect.poll(async () => (await page.getByLabel('Workflow steps').textContent()) ?? '').toMatch(/use p\.seg\.check-and-screen\.v1[\s\S]*p\.screen\.sanctions\.v1/);
    await page.getByRole('button', { name: 'Breakpoint at route' }).click();
    await bar().getByRole('button', { name: 'Continue ⏭' }).click();
    expect(await what()).toMatch(/Step route ran p\.decide\.rail\.v1@1\.0\.0\. Its outcome is decided/);
    expect((await page.getByTestId('dbg-decisions').textContent()) ?? '').toMatch(/rail.*"uae\.wps".*p\.table\.rail-choice\.v1/s);
    expect((await page.getByTestId('dbg-changed').textContent()) ?? '').toMatch(/facts\.route.*\(none\).*"rail":"uae\.wps"/s);
    expect(await page.locator('.dbg-outline li.dbg-here').getAttribute('data-step')).toBe('route');
  });

  it('steps into a bank hook, then out again; step over passes a leg at one go', async () => {
    for (let i = 0; i < 6 && !/\(hook\)/.test((await page.locator('.dbg-chain').textContent()) ?? ''); i++) await bar().getByRole('button', { name: 'Step into ▶' }).click();
    expect((await page.locator('.dbg-chain').textContent()) ?? '').toMatch(/p\.wf\.corporate\.v1.*›.*b\.hook\.pre-release-approval\.v1/s);
    await bar().getByRole('button', { name: 'Step out ⤴' }).click();
    expect((await page.locator('.dbg-chain').textContent()) ?? '').not.toMatch(/›/);
    // The leg is a child instance: step over goes past all its events.
    while (!/the leg uae\.wps\.wf\.salary-file-out\.v1/.test(await what())) await bar().getByRole('button', { name: 'Step over ⤼' }).click();
    const before = await pos();
    await bar().getByRole('button', { name: 'Step over ⤼' }).click();
    expect((await page.locator('.dbg-chain').textContent()) ?? '').not.toMatch(/salary-file-out/);
    expect(await pos()).toBeGreaterThan(before + 5);
  });

  it('the data now marks what changed; every event shows more; the run report is a click away', async () => {
    // A message reaching the front door belongs to no instance yet; step on to a workflow's event.
    for (let i = 0; i < 6 && !/header/.test((await page.getByTestId('dbg-data').textContent()) ?? ''); i++) await bar().getByRole('button', { name: 'Step into ▶' }).click();
    expect(await page.getByTestId('dbg-data').textContent()).toMatch(/header/);
    const n = Number(/of (\d+)/.exec((await page.locator('.dbg-pos').textContent()) ?? '')?.[1]);
    await page.getByLabel('every event').check();
    const m = Number(/of (\d+)/.exec((await page.locator('.dbg-pos').textContent()) ?? '')?.[1]);
    expect(m).toBeGreaterThan(n);
    // No breakpoint is ahead: Continue runs to the last event. That is the bank's notify hook at
    // on-close, which runs alongside and ends after the payment (R-06-09).
    await bar().getByRole('button', { name: 'Continue ⏭' }).click();
    expect(await pos()).toBe(m);
    expect(await what()).toMatch(/on-close/);
    expect((await page.locator('.dbg-events').textContent()) ?? '').toMatch(/p\.wf\.corporate\.v1 ended as settled/);
    await page.getByRole('button', { name: 'Show the run report' }).click();
    await page.getByText('Business trace').first().waitFor();
    expect(problems).toEqual([]);
  });
});
