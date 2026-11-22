// The test bench's web UI in a real browser (Playwright, Chromium): sign in through the developer identity provider
// (authorization code with PKCE, TH_AUTH_DEV=1), run a profile on a library sample, and see the results side by side
// with the interpreter; then a caller without the Tester role is refused. Run from runtime/testbench/web:
//   npm run build && npm run test:ui
// It starts the test bench service itself (dotnet run), on a free port, with its run history in a temporary folder.
// Playwright is the one installed on the machine (require('playwright')), or PLAYWRIGHT_MODULE names it.

import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as net from 'node:net';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const RUNTIME = path.resolve(here, '..', '..', '..');
const PROJECT = path.join(RUNTIME, 'tools', 'TransactHub.TestBench');
const local = fs.mkdtempSync(path.join(os.tmpdir(), 'th-testbench-ui-'));
let service, browser, url;

function playwright() {
  for (const m of [process.env.PLAYWRIGHT_MODULE, 'playwright', '/opt/node-tools/node_modules/playwright'].filter(Boolean)) {
    try { return require(m); } catch { /* next */ }
  }
  throw new Error('Playwright is not installed: npm install -g playwright, or set PLAYWRIGHT_MODULE');
}

const freePort = () => new Promise((resolve) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => resolve(p)); }); });

before(async () => {
  assert.ok(fs.existsSync(path.join(here, '..', 'dist', 'index.html')), 'Build the UI first: npm run build');
  const b = spawnSync('dotnet', ['build', '-v', 'q', '--nologo'], { cwd: PROJECT, encoding: 'utf8' });
  assert.equal(b.status, 0, b.stdout);
  const port = await freePort();
  url = `http://127.0.0.1:${port}`;
  service = spawn('dotnet', [path.join(PROJECT, 'bin', 'Debug', 'net10.0', 'testbench.dll'), 'serve', '--urls', url], {
    cwd: RUNTIME, env: { ...process.env, TH_AUTH_DEV: '1', TH_TESTBENCH_LOCAL: local, ASPNETCORE_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe'],
  });
  let log = '';
  service.stdout.on('data', (d) => (log += d));
  service.stderr.on('data', (d) => (log += d));
  for (let i = 0; ; i++) {
    try { if ((await fetch(`${url}/api/health`)).ok) break; } catch { /* not up yet */ }
    if (i > 120 || service.exitCode !== null) throw new Error(`The test bench service did not start:\n${log}`);
    await new Promise((r) => setTimeout(r, 500));
  }
  browser = await playwright().chromium.launch();
});

after(async () => {
  await browser?.close();
  service?.kill();
  fs.rmSync(local, { recursive: true, force: true });
});

async function signIn(page, groups = ['th-testers']) {
  await page.goto(url);
  await page.getByTestId('sign-in').click();
  await page.waitForURL(/\/dev-idp\/authorize/);
  await page.fill('input[name=user]', 'ui.tester');
  for (const g of ['th-testers', 'th-operators', 'th-supervisors', 'th-readers']) {
    const box = page.locator(`input[name=groups][value=${g}]`);
    if (groups.includes(g)) await box.check(); else await box.uncheck();
  }
  await page.click('#sign-in');
  await page.waitForURL(`${url}/`);
}

test('sign in, run a profile on a library sample, see each run beside the interpreter, save it as a case', { timeout: 600000 }, async () => {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await signIn(page);
  await page.getByTestId('who').waitFor();
  assert.match(await page.getByTestId('who').innerText(), /ui\.tester · tester/);

  await page.getByTestId('profile').selectOption('wps-quick-dtfx');
  await page.getByTestId('sample').selectOption('wps-sif-happy');
  await page.getByTestId('run').click();
  await page.getByTestId('result').waitFor();
  await page.getByTestId('status').filter({ hasText: /passed|failed/ }).waitFor({ timeout: 540000 });
  assert.equal(await page.getByTestId('status').innerText(), 'passed', await page.getByTestId('result').innerText());
  const ref = await page.getByTestId('row-interpreter').innerText();
  const dtfx = await page.getByTestId('row-dtfx').innerText();
  assert.match(ref, /settled/);
  assert.match(dtfx, /settled\s+yes/);
  assert.match(await page.getByTestId('trace-dtfx').innerText(), /end:settled/);

  await page.getByTestId('case-id').fill('TB-UI-001');
  await page.getByTestId('save-case').click();
  await page.getByTestId('saved').waitFor();
  assert.match(await page.getByTestId('saved').innerText(), /TB-UI-001\.yaml/);
  assert.ok(fs.existsSync(path.join(local, 'cases', 'TB-UI-001.yaml')));

  await page.getByTestId('nav-history').click();
  await page.getByTestId('history').getByText('wps-quick-dtfx').first().waitFor();
  assert.deepEqual(errors, []);
});

test('the builder makes a valid file for the bank, and an injected error is found before running', { timeout: 120000 }, async () => {
  const page = await browser.newPage();
  await signIn(page);
  await page.getByTestId('nav-data').click();
  await page.getByTestId('library').getByText('wps-sif-nak').waitFor();
  await page.getByTestId('build').click();
  await page.getByTestId('validation').waitFor();
  assert.match(await page.getByTestId('validation').innerText(), /^Valid/);
  assert.match(await page.getByTestId('built').inputValue(), /^# file: \d{13}\d{12}\.sif\nSCR,\d{13},302620122,/);
  await page.getByLabel('invalid-iban').check();
  await page.getByTestId('build').click();
  await page.getByTestId('validation').getByText('TB-F10', { exact: false }).waitFor();
});

test('a caller the bank gives no tester role is refused', { timeout: 120000 }, async () => {
  const page = await browser.newPage();
  await signIn(page, ['th-readers']);
  await page.getByTestId('error').waitFor();
  assert.match(await page.getByTestId('error').innerText(), /roles do not allow/);
});
