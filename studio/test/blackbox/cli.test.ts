import { spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { STUDIO, startServer } from './server.js';
import { unzip } from '../../src/export/zip.js';

// Black box: the studio's command line, by output and exit code.

function studio(...args: string[]) {
  const r = spawnSync(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), ['src/cli.ts', ...args], { cwd: STUDIO, encoding: 'utf8', env: { ...process.env }, timeout: 300000 });
  return { code: r.status, out: r.stdout ?? '', err: r.stderr ?? '' };
}
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'th-cli-'));

describe('studio command line', () => {
  it('prints its usage, and exits 2 for an unknown command', () => {
    const help = studio('help');
    expect(help.code).toBe(0);
    expect(help.out).toContain('Usage: studio [--bank <name> ...] test');
    const bad = studio('frobnicate');
    expect(bad.code).toBe(2);
    expect(bad.err).toContain('Unknown command frobnicate');
  });

  it('runs every factory and bank test and exits 0', () => {
    const r = studio('test');
    expect(r.code, r.out.split('\n').filter((l) => l.startsWith('FAIL')).join('\n')).toBe(0);
    expect(r.out).toMatch(/(\d+) of \1 tests pass/);
  }, 300000);

  it('shows the checks of one test, and exits 1 for a test that does not exist', () => {
    const one = studio('test', 'WPS-OUT-002');
    expect(one.code).toBe(0);
    expect(one.out).toContain('reasonCode: expected "AM10" got "AM10"');
    expect(one.out).toContain('trace: ');
    const none = studio('test', 'NO-SUCH-001');
    expect(none.code).toBe(1);
    expect(none.err).toContain('No test NO-SUCH-001');
  }, 120000);

  it('runs the conformance kit, whole and by scenario', () => {
    const all = studio('kit');
    expect(all.code).toBe(0);
    expect(all.out).toContain('56 of 56 conformance scenarios pass');
    const one = studio('kit', 'CK-SEQ-01');
    expect(one.out).toContain('1 of 1 conformance scenarios pass');
  }, 300000);

  it('checks every artefact with no errors, and says which content it read', () => {
    const r = studio('check');
    expect(r.code).toBe(0);
    expect(r.out).toMatch(/^0 error\(s\), \d+ warning\(s\)$/m);
    // Not connected: an in-memory fake GitHub seeded from content-configurations/ (R-11-21).
    expect(r.err).toMatch(/^content: an in-memory branch seeded from .*content-configurations/m);
  }, 120000);

  it('reports the seed current in content-configurations/ (R-11-25)', () => {
    const r = studio('seed-content');
    expect(r.code, r.out).toBe(0);
    expect(r.out).toMatch(/^(\d+) of \1 seeded files are current/m);
  }, 120000);

  it('prints an export manifest and writes the same package twice, signed each time', () => {
    const m = studio('export', 'p.wf.commercial.v1', 'flowable');
    expect(m.code).toBe(0);
    expect(JSON.parse(m.out).target).toBe('flowable');
    const a = path.join(work, 'a.zip');
    const b = path.join(work, 'b.zip');
    expect(studio('export-zip', 'uae.wps.wf.salary-file-out.v1', 'dtfx', a).out).toContain('workers th-p, th-uae-p, th-uae-wps');
    expect(studio('export-zip', 'uae.wps.wf.salary-file-out.v1', 'dtfx', b).out).toMatch(/signed by key [0-9a-f]{32}/);
    // Every file the same, byte for byte; an ECDSA signature differs each time it is made (R-09-23).
    const files = (z: string) => unzip(fs.readFileSync(z)).filter((e) => e.name !== 'release.json' && e.name !== 'release.sig').map((e) => `${e.name} ${e.content.toString('base64')}`);
    expect(files(a)).toEqual(files(b));
    expect(unzip(fs.readFileSync(a)).map((e) => e.name)).toEqual(expect.arrayContaining(['release.json', 'release.sig']));
  }, 120000);

  it('refuses a deploy with missing or malformed arguments, with exit code 2', () => {
    expect(studio('deploy').code).toBe(2);
    expect(studio('deploy', 'x.zip').err).toContain('--workflow');
    const bench = studio('deploy', 'x.zip', '--workflow', 'w', '--bench', 'nonsense');
    expect(bench.code).toBe(2);
    expect(bench.err).toContain('port=simulator:scenario');
    expect(studio('export-zip', 'uae.wps.wf.salary-file-out.v1').code).toBe(2);
  }, 60000);

  it('fails an export of a workflow that does not resolve with exit code 1', () => {
    const r = studio('export-zip', 'nope.wf.x.v1', 'dtfx', path.join(work, 'n.zip'));
    expect(r.code).toBe(1);
    expect(r.err).toContain('nope.wf.x.v1 does not resolve');
  });

  it('signs in by device authorization, and the studio decides its calls as the signed-in user (R-11-46)', async () => {
    const server = await startServer();
    const env = { ...process.env, TH_STUDIO_TOKEN_FILE: path.join(work, 'token.json') };
    try {
      // No sign-in yet: the command says so and exits 1.
      // The fake GitHub answers in this process, so every command runs without blocking it.
      const run = (a: string[], e: NodeJS.ProcessEnv = env) => new Promise<{ status: number | null; stdout: string; stderr: string }>((resolve) => {
        const c = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), ['src/cli.ts', ...a], { cwd: STUDIO, env: e });
        let stdout = '';
        let stderr = '';
        c.stdout.on('data', (d) => (stdout += d));
        c.stderr.on('data', (d) => (stderr += d));
        c.on('exit', (status) => resolve({ status, stdout, stderr }));
      });
      const before = await run(['whoami', '--server', server.url]);
      expect(before.status).toBe(1);
      expect(before.stderr).toContain('Not signed in');
      const child = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), ['src/cli.ts', 'login', '--server', server.url], { cwd: STUDIO, env });
      let out = '';
      let err = '';
      child.stdout.on('data', (d) => (out += d));
      child.stderr.on('data', (d) => (err += d));
      for (let i = 0; i < 300 && !/enter the code/.test(err); i++) await new Promise((r) => setTimeout(r, 100));
      const m = /open (\S+) and enter the code (\S+)/.exec(err)!;
      expect(m, err).toBeTruthy();
      // The operator opens the provider's page and signs in as a bank approver.
      expect((await fetch(m[1], { method: 'POST', body: new URLSearchParams({ user_code: m[2], user: 'example.checker' }) })).status).toBe(200);
      const code = await new Promise<number | null>((r) => child.on('exit', r));
      expect(code, err).toBe(0);
      expect(out).toContain('signed in to');
      expect(out).toContain('example.checker (example-bank): bank approver');
      expect((await run(['whoami', '--server', server.url])).stdout).toContain('example.checker (example-bank)');
      // The acting user is the token's: --actor is refused with a studio named.
      const zip = path.join(work, 'bundle.zip');
      expect((await run(['import-sample', zip])).status).toBe(0);
      const actor = await run(['import', zip, '--server', server.url, '--actor', 'shared.designer', '--apply']);
      expect(actor.status).toBe(2);
      expect(actor.stderr).toContain('--actor and --bank-actor are refused');
      // The permission table decides: a bank approver runs no import, from the command line as from the web app.
      const shared = await run(['import', zip, '--server', server.url, '--apply']);
      expect(shared.status).toBe(1);
      expect(shared.stderr).toMatch(/^403 /m);
      // Without a studio, applying to a connected branch is refused: nothing proves who typed it.
      const direct = await run(['import', zip, '--apply'], { ...env, TH_GITHUB_API: server.fake.url, TH_CONTENT_REPO: server.repo, TH_CONTENT_BRANCH: 'main', TH_GITHUB_TOKEN: 'synthetic-test-token' });
      expect(direct.stderr + direct.stdout).toContain('needs a signed-in user');
      expect((await run(['logout', '--server', server.url])).status).toBe(0);
      expect(fs.existsSync(env.TH_STUDIO_TOKEN_FILE)).toBe(false);
    } finally {
      server.stop();
    }
  }, 300000);
});
