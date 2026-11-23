import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

// The Test bench section of the runtime's web app, end to end in a real browser (Playwright's Chromium): the test bench
// service (runtime/tools/TransactHub.TestBench) serves the built app, a tester signs in with the shell's one sign-in
// (dev mode here, TH_AUTH_DEV=1; the bank's identity provider everywhere else), runs a profile on a library sample on
// DTFx beside the studio interpreter, sees both results, and saves the run as a regression case; the builder makes a
// valid file and finds an injected error; a user the role map gives no Tester role is refused.

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const CONSOLE = path.resolve(here, '..', '..');
const RUNTIME = path.resolve(CONSOLE, '..');
const PROJECT = path.join(RUNTIME, 'tools', 'TransactHub.TestBench');
const local = fs.mkdtempSync(path.join(os.tmpdir(), 'th-testbench-ui-'));
let service: ChildProcess | undefined;
let browser: any;
let url = '';
let log = '';

const freePort = () => new Promise<number>((resolve) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as net.AddressInfo).port; s.close(() => resolve(p)); }); });

beforeAll(async () => {
  expect(fs.existsSync(path.join(CONSOLE, 'dist', 'index.html')), 'Build the app first: npm run build').toBe(true);
  const b = spawnSync('dotnet', ['build', '-v', 'q', '--nologo'], { cwd: PROJECT, encoding: 'utf8', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
  expect(b.status, b.stdout).toBe(0);
  url = `http://127.0.0.1:${await freePort()}`;
  service = spawn('dotnet', [path.join(PROJECT, 'bin', 'Debug', 'net10.0', 'testbench.dll'), 'serve', '--urls', url], {
    cwd: RUNTIME, env: { ...process.env, TH_AUTH_DEV: '1', TH_TESTBENCH_LOCAL: local, ASPNETCORE_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe'],
  });
  service.stdout!.on('data', (d) => (log += d));
  service.stderr!.on('data', (d) => (log += d));
  for (let i = 0; ; i++) {
    try { if ((await fetch(`${url}/testbench/api/health`)).ok) break; } catch { /* not up yet */ }
    if (i > 120 || service.exitCode !== null) throw new Error(`The test bench service did not start:\n${log}`);
    await new Promise((r) => setTimeout(r, 500));
  }
  browser = await require(process.env.PLAYWRIGHT_MODULE ?? 'playwright').chromium.launch();
});

afterAll(async () => {
  await browser?.close();
  service?.kill();
  fs.rmSync(local, { recursive: true, force: true });
});

async function signIn(as: RegExp, slowTemplates = false) {
  const page = await browser.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e: Error) => errors.push(e.message));
  // The bench's templates may arrive after its library (a busy host): the builder must wait for them, not send a recipe with none.
  if (slowTemplates) await page.route('**/testbench/api/data/templates', async (r: any) => { await new Promise((x) => setTimeout(x, 2000)); await r.continue(); });
  await page.goto(`${url}/#/testbench`);
  await page.getByRole('button', { name: as }).click();
  return { page, errors };
}

describe('the Test bench section of the runtime web app', () => {
  it('a tester signs in once, runs a profile on a sample, sees DTFx beside the interpreter, and saves the run as a case', async () => {
    const { page, errors } = await signIn(/Sign in as Example tester/);
    expect(await page.getByTestId('who').innerText()).toContain('Example tester');
    await page.getByRole('link', { name: 'Test bench' }).waitFor();
    await page.getByTestId('profile').selectOption('wps-quick-dtfx');
    await page.getByTestId('sample').selectOption('wps-sif-happy');
    await page.getByTestId('run').click();
    await page.getByTestId('result').waitFor();
    await page.getByTestId('status').filter({ hasText: /passed|failed/ }).waitFor({ timeout: 540000 });
    expect(await page.getByTestId('status').innerText(), await page.getByTestId('result').innerText()).toBe('passed');
    expect(await page.getByTestId('row-interpreter').innerText()).toMatch(/settled/);
    expect(await page.getByTestId('row-dtfx').innerText()).toMatch(/settled\s+yes/);
    expect(await page.getByTestId('trace-dtfx').innerText()).toMatch(/end:settled/);
    await page.getByTestId('case-id').fill('TB-UI-001');
    await page.getByTestId('save-case').click();
    await page.getByTestId('saved').waitFor();
    expect(await page.getByTestId('saved').innerText()).toMatch(/TB-UI-001\.yaml/);
    expect(fs.existsSync(path.join(local, 'cases', 'TB-UI-001.yaml'))).toBe(true);
    await page.getByTestId('nav-history').click();
    await page.getByTestId('history').getByText('wps-quick-dtfx').first().waitFor();
    expect(errors).toEqual([]);
  });

  it('the builder makes a valid file for the bank, and an injected error is found before running', async () => {
    const { page } = await signIn(/Sign in as Example tester/, true);
    await page.getByTestId('nav-data').click();
    await page.getByTestId('library').getByText('wps-sif-nak').waitFor();
    await page.getByTestId('build').click();
    await page.getByTestId('validation').waitFor();
    expect(await page.getByTestId('validation').innerText()).toMatch(/^Valid/);
    expect(await page.getByTestId('built').inputValue()).toMatch(/^# file: \d{25}\.sif\nSCR,\d{13},302620122,/);
    await page.getByLabel('invalid-iban').check();
    await page.getByTestId('build').click();
    await page.getByTestId('validation').getByText('TB-F10', { exact: false }).waitFor();
  });

  it('a user the role map gives no Tester role is refused', async () => {
    const { page } = await signIn(/Sign in as Example reader without a role/);
    await page.getByTestId('testbench-refused').waitFor();
    expect(await page.getByTestId('testbench-refused').innerText()).toMatch(/roles do not allow the test bench/);
  });
});
