import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, describe, expect, it } from 'vitest';
import { bundleZip, wpsTrainingBundle } from '../src/import/sample.js';
import { parseYaml, toYaml } from '../src/core/yaml.js';
import { client, startServer, STUDIO, type Server } from './blackbox/server.js';
import { freshEnv } from './helpers.js';

// Memory only (R-11-24): the studio, as its own process with a $HOME of its own, runs a full flow
// against the fake GitHub (draft, approve, publish, import, a configuration edit, a waiver, a script
// and a sample) and is started again. It writes no file: not in studio/, not in its $HOME, not in the
// repository's content-configurations/ or spec/. Its drafts survive the restart, on their branches.

const REPO = path.resolve(STUDIO, '..');
const WATCHED = [STUDIO, path.join(REPO, 'content-configurations'), path.join(REPO, 'spec')];
const SKIP = new Set([path.join(STUDIO, 'node_modules'), path.join(STUDIO, 'coverage')]);

function snapshot(dirs: string[]): Map<string, string> {
  const out = new Map<string, string>();
  const walk = (d: string) => {
    if (SKIP.has(d) || !fs.existsSync(d)) return;
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) walk(p);
      else if (e.isFile()) {
        const s = fs.statSync(p);
        out.set(p, `${s.size}:${s.mtimeMs}`);
      }
    }
  };
  for (const d of dirs) walk(d);
  return out;
}

function changes(a: Map<string, string>, b: Map<string, string>): string[] {
  const out: string[] = [];
  for (const [p, v] of b) if (a.get(p) !== v) out.push(`${a.has(p) ? 'changed' : 'added'} ${p}`);
  for (const p of a.keys()) if (!b.has(p)) out.push(`removed ${p}`);
  return out.sort();
}

let servers: Server[] = [];
afterAll(() => servers.forEach((s) => s.stop()));

describe('the studio writes nothing to disk (R-11-24)', () => {
  it('a full flow and a restart leave studio/, its $HOME and the repository untouched; the drafts are on their branches', async () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), 'th-home-'));
    const before = snapshot(WATCHED);
    const env = { HOME: home, USERPROFILE: home, XDG_CONFIG_HOME: path.join(home, '.config'), XDG_CACHE_HOME: path.join(home, '.cache') };
    const server = await startServer(env);
    servers.push(server);
    const api = client(server.url);
    const ok = (r: { status: number; data: any }, what: string) => {
      expect(r.status, `${what}: ${JSON.stringify(r.data).slice(0, 400)}`).toBe(200);
      return r.data;
    };

    // Draft, review, approve and publish.
    const doc = {
      kind: 'activity', name: 'p.enrich.no-disk-note.v1', release: '1.0.0', title: 'Note', type: 'enrich', reads: ['header'], writes: ['facts.note'],
      outcomes: [{ name: 'done', primary: true }], effect: 'pure', implementation: { binding: 'builtin', service: 'noop' },
      provenance: { origin: 'authored', status: 'provisional', clauses: ['No disk write test'] },
    };
    const d = ok(await api.post('/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc }), 'draft');
    ok(await api.post('/api/review', { tenant: 'owner', actor: 'shared.designer', key: d.draft.key }), 'review');
    const pub = ok(await api.post('/api/publish', { tenant: 'owner', key: d.draft.key, checker: 'shared.checker' }), 'publish');
    expect(pub.git.status).toBe('committed');

    // An import: documents, scripts, samples and tests, saved as drafts on their change set branches.
    const bundle = bundleZip(wpsTrainingBundle(freshEnv())).toString('base64');
    const imp = ok(await api.post('/api/import/apply', { tenant: 'owner', bundle, bankTenant: 'example-bank', actor: 'shared.designer', bankActor: 'example.maker' }), 'import');
    expect(imp.applied).toBe(true);
    const owner = imp.changeSets.find((set: string[]) => !set[0].startsWith('example-bank/'));
    ok(await api.post('/api/publish', { tenant: 'owner', key: owner[0], checker: 'shared.checker' }), 'publish an imported change set');

    // A configuration edit and a waiver.
    const text = server.fake.files(server.repo, 'main')['content-configurations/banks/example-bank/configuration-set.yaml'];
    const cs = parseYaml(text.slice(text.indexOf('\n') + 1));
    ok(await api.post('/api/tenants/example-bank/configuration', { actor: 'example.maker', yaml: toYaml({ ...cs, release: '9.0.0' }) }), 'configuration');
    ok(await api.post('/api/conformance/waivers', { actor: 'shared.checker', target: 'studio', scenario: 'CK-SEQ-01', reason: 'No disk write test' }), 'waiver');
    const drafts = [...ok(await api.get('/api/drafts?tenant=owner'), 'drafts'), ...ok(await api.get('/api/drafts?tenant=example-bank'), 'bank drafts')].map((x: any) => x.key).sort();
    expect(drafts.length).toBeGreaterThan(0);

    // Started again on the same repository: everything it knows is read from the fake GitHub.
    server.stop();
    const again = await startServer(env, { repo: server.repo });
    servers.push(again);
    const api2 = client(again.url);
    const after = [...ok(await api2.get('/api/drafts?tenant=owner'), 'drafts'), ...ok(await api2.get('/api/drafts?tenant=example-bank'), 'bank drafts')].map((x: any) => x.key).sort();
    expect(after).toEqual(drafts);
    expect(ok(await api2.get('/api/change-sets'), 'change sets').map((c: any) => c.id)).toContain(pub.changeSet.id);
    again.stop();

    // The evidence: what the studio wrote is on the branches of the fake, and nothing is on disk.
    const branches = server.fake.branches(server.repo);
    expect(branches.filter((b) => b.startsWith('studio/')).length).toBeGreaterThanOrEqual(imp.changeSets.length);
    const all = branches.flatMap((b) => Object.keys(server.fake.files(server.repo, b)));
    const main = Object.keys(server.fake.files(server.repo, 'main'));
    expect(all.some((p) => /^content-configurations\/(banks\/[^/]+\/)?scripts\//.test(p)), 'a script on a branch').toBe(true);
    expect(main.some((p) => p.startsWith('content-configurations/samples/')), 'a sample on the content branch').toBe(true);
    expect(main).toContain('content-configurations/banks/example-bank/configuration-sets/9.0.0.yaml');
    expect(main.filter((p) => p.startsWith('content-configurations/change-sets/')).length).toBeGreaterThanOrEqual(4); // the seed, two publishes, a waiver
    expect(main.some((p) => p.startsWith('content-configurations/audit/events/'))).toBe(true);
    expect(changes(before, snapshot(WATCHED))).toEqual([]);
    expect(fs.readdirSync(home)).toEqual([]);
  }, 240000);
});
