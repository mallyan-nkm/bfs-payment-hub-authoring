import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { STUDIO } from './server.js';

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
    expect(all.out).toContain('46 of 46 conformance scenarios pass');
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

  it('prints an export manifest and writes the same zip twice', () => {
    const m = studio('export', 'p.wf.commercial.v1', 'flowable');
    expect(m.code).toBe(0);
    expect(JSON.parse(m.out).target).toBe('flowable');
    const a = path.join(work, 'a.zip');
    const b = path.join(work, 'b.zip');
    expect(studio('export-zip', 'uae.wps.wf.salary-file-out.v1', 'dtfx', a).out).toContain('workers th-uae-p, th-uae-wps');
    studio('export-zip', 'uae.wps.wf.salary-file-out.v1', 'dtfx', b);
    expect(fs.readFileSync(a).equals(fs.readFileSync(b))).toBe(true);
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
});
