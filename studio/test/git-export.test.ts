import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { parseYaml } from '../src/core/yaml.js';
import { initBareRepo } from '../src/export/git.js';
import { apply, readBundle } from '../src/import/bundle.js';
import { wpsTrainingBundle } from '../src/import/sample.js';
import { dispatch } from '../src/server/api.js';
import { freshEnv } from './helpers.js';

// Export to a connected Git branch (plan W8; R-09-19 to R-09-22), against a local bare repository.

const call = (env: any, method: string, p: string, body: any = {}, query: any = {}) => dispatch(env, { method, path: p, query, body });
const git = (cwd: string, ...args: string[]) => {
  const r = spawnSync('git', ['-c', 'user.name=Someone', '-c', 'user.email=someone@example.invalid', ...args], { cwd, encoding: 'utf8' });
  if (r.status !== 0) throw new Error(r.stderr);
  return r.stdout.trim();
};
const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'th-git-test-'));
/** A fresh clone of the remote's branch, to look at what the studio wrote. */
const look = (remote: string, branch = 'main') => {
  const d = path.join(tmp(), 'look');
  git(os.tmpdir(), 'clone', '--quiet', '--branch', branch, remote, d);
  return d;
};

const ACT = (n: string) => ({
  kind: 'activity', name: `p.enrich.git-note-${n}.v1`, release: '1.0.0', title: 'Note', type: 'enrich', reads: ['header'], writes: ['facts.note'],
  outcomes: [{ name: 'done', primary: true }], effect: 'pure', implementation: { binding: 'builtin', service: 'noop' },
  provenance: { origin: 'authored', status: 'provisional', clauses: ['Git export test'] },
});

async function publishActivity(env: any, n: string) {
  const d = await call(env, 'POST', '/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: ACT(n) });
  expect(d.status, JSON.stringify(d.body)).toBe(200);
  await call(env, 'POST', '/api/review', { tenant: 'owner', actor: 'shared.designer', key: d.body.draft.key });
  const p = await call(env, 'POST', '/api/publish', { tenant: 'owner', key: d.body.draft.key, checker: 'shared.checker' });
  expect(p.status, JSON.stringify(p.body)).toBe(200);
  return p.body;
}

const saved = process.env.TH_GITHUB_TOKEN;
afterEach(() => {
  if (saved === undefined) delete process.env.TH_GITHUB_TOKEN;
  else process.env.TH_GITHUB_TOKEN = saved;
});

describe('connecting a branch', () => {
  it('only a shared checker or service operations connects, and only to a branch that exists', async () => {
    const env = freshEnv();
    const remote = initBareRepo(tmp());
    expect((await call(env, 'PUT', '/api/git', { actor: 'shared.designer', remote, branch: 'main' })).status).toBe(409);
    expect((await call(env, 'PUT', '/api/git', { actor: 'example.checker', remote, branch: 'main' })).body.error).toMatch(/owner/);
    expect((await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote, branch: 'release' })).body.error).toMatch(/no branch release.*never creates/);
    expect((await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote: 'ftp://example.invalid/x', branch: 'main' })).body.error).toMatch(/Not a repository/);
    expect((await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote, branch: 'a..b' })).body.error).toMatch(/Not a branch/);
    expect((await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote: path.join(tmp(), 'missing.git'), branch: 'main' })).body.error).toMatch(/cannot read/);

    const ok = await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote, branch: 'main' });
    expect(ok.status, JSON.stringify(ok.body)).toBe(200);
    expect(ok.body.connection).toMatchObject({ remote, branch: 'main', mode: 'commit', target: 'temporal', autoExport: true, connectedBy: 'shared.checker' });
    expect((await call(env, 'GET', '/api/git')).body).toMatchObject({ connection: { remote }, tokenNeeded: false, exports: [] });
    expect(env.store.audit.some((a: any) => a.action === 'connect-git' && a.outcome === 'refused')).toBe(true);

    expect((await call(env, 'DELETE', '/api/git', {}, { actor: 'shared.checker' })).status).toBe(200);
    expect((await call(env, 'GET', '/api/git')).body.connection).toBeNull();
  });

  it('a GitHub remote needs the token in the environment, and the token is never stored or shown', async () => {
    const env = freshEnv();
    delete process.env.TH_GITHUB_TOKEN;
    const r = await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote: 'https://github.com/example-org/content-configurations', branch: 'main' });
    expect(r.body.error).toMatch(/Set TH_GITHUB_TOKEN/);
    process.env.TH_GITHUB_TOKEN = 'synthetic-token-value';
    const remote = initBareRepo(tmp());
    await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote, branch: 'main' });
    const status = await call(env, 'GET', '/api/git');
    expect(status.body.tokenPresent).toBe(true);
    expect(JSON.stringify(status.body)).not.toContain('synthetic-token-value');
    expect(fs.readFileSync(path.join(env.store.dataRoot, 'workspace.json'), 'utf8')).not.toContain('synthetic-token-value');
  });
});

describe('exporting a published change set', () => {
  it('writes one commit on publish: the documents at their releases and the change set record', async () => {
    const env = freshEnv();
    const remote = initBareRepo(tmp());
    await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote, branch: 'main' });
    const pub = await publishActivity(env, 'a');
    expect(pub.git).toMatchObject({ status: 'committed', branch: 'main', changeSet: pub.changeSet.id });
    expect(pub.git.files).toEqual([`artefacts/p/p.enrich.git-note-a.v1/1.0.0.yaml`, `change-sets/${pub.changeSet.id}.yaml`]);

    const d = look(remote);
    expect(parseYaml(fs.readFileSync(path.join(d, 'artefacts/p/p.enrich.git-note-a.v1/1.0.0.yaml'), 'utf8'))).toMatchObject({ name: 'p.enrich.git-note-a.v1', release: '1.0.0' });
    expect(parseYaml(fs.readFileSync(path.join(d, `change-sets/${pub.changeSet.id}.yaml`), 'utf8'))).toMatchObject({ kind: 'change-set', checker: 'shared.checker', makers: ['shared.designer'] });
    expect(git(d, 'rev-list', '--count', 'HEAD')).toBe('2');
    expect(git(d, 'log', '-1', '--format=%an')).toBe('TransactHub Studio');
    const msg = git(d, 'log', '-1', '--format=%B');
    expect(msg).toMatch(new RegExp(`^Release change set ${pub.changeSet.id}: 1 artefact`));
    expect(msg).toMatch(/Maker: shared\.designer\nChecker: shared\.checker/);
    expect(git(d, 'rev-parse', 'HEAD')).toBe(pub.git.commit);
    expect((await call(env, 'GET', '/api/git')).body.exports[0]).toMatchObject({ changeSet: pub.changeSet.id, status: 'committed' });
  });

  it('exporting again changes nothing; a rewrite of a file it wrote is refused and named; it never forces', async () => {
    const env = freshEnv();
    const remote = initBareRepo(tmp());
    await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote, branch: 'main' });
    const pub = await publishActivity(env, 'b');
    const again = await call(env, 'POST', '/api/git/export', { actor: 'shared.checker', changeSet: pub.changeSet.id });
    expect(again.body).toMatchObject({ status: 'unchanged', files: [] });

    // Someone edits the released file on the branch, and adds an unrelated one.
    const d = look(remote);
    const f = path.join(d, 'artefacts/p/p.enrich.git-note-b.v1/1.0.0.yaml');
    fs.writeFileSync(f, fs.readFileSync(f, 'utf8').replace('title: Note', 'title: Edited on the branch'));
    fs.writeFileSync(path.join(d, 'NOTES.md'), 'unrelated\n');
    git(d, 'commit', '--quiet', '-am', 'Edit a release by hand');
    git(d, 'add', 'NOTES.md');
    git(d, 'commit', '--quiet', '-m', 'Notes');
    git(d, 'push', '--quiet', 'origin', 'main');
    const head = git(d, 'rev-parse', 'HEAD');

    const r = await call(env, 'POST', '/api/git/export', { actor: 'shared.checker', changeSet: pub.changeSet.id });
    expect(r.status).toBe(409);
    expect(r.body.code).toBe('R-09-21');
    expect(r.body.error).toMatch(/artefacts\/p\/p\.enrich\.git-note-b\.v1\/1\.0\.0\.yaml.*never rewritten/);
    expect(git(look(remote), 'rev-parse', 'HEAD')).toBe(head); // nothing pushed, nothing forced

    // A later change set still exports on top of the moved branch.
    const next = await publishActivity(env, 'c');
    expect(next.git.status).toBe('committed');
    const d2 = look(remote);
    expect(git(d2, 'rev-parse', 'HEAD~1')).toBe(head);
    expect(fs.existsSync(path.join(d2, 'NOTES.md'))).toBe(true);
  });

  it('an auditor exports nothing, and a change set that does not exist is not found', async () => {
    const env = freshEnv();
    const remote = initBareRepo(tmp());
    await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote, branch: 'main', autoExport: false });
    const pub = await publishActivity(env, 'd');
    expect(pub.git).toBeUndefined(); // not exported on publish
    expect((await call(env, 'POST', '/api/git/export', { actor: 'shared.auditor', changeSet: pub.changeSet.id })).body.error).toMatch(/auditor/);
    expect((await call(env, 'POST', '/api/git/export', { actor: 'shared.checker', changeSet: 'CS-1999-01-01-001' })).status).toBe(409);
    expect((await call(env, 'POST', '/api/git/export', { actor: 'shared.checker', changeSet: pub.changeSet.id })).body.status).toBe('committed');
  });

  it('in pull-request mode, pushes the change set to its own branch and leaves the connected branch alone', async () => {
    const env = freshEnv();
    const remote = initBareRepo(tmp());
    const before = git(look(remote), 'rev-parse', 'HEAD');
    await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote, branch: 'main', mode: 'pull-request' });
    const pub = await publishActivity(env, 'e');
    const branch = `studio/${pub.changeSet.id.toLowerCase()}`;
    expect(pub.git).toMatchObject({ status: 'pull-request', branch, pullRequest: { branch, opened: false } });
    expect(pub.git.pullRequest.reason).toMatch(/Not a GitHub remote/);
    expect(git(look(remote), 'rev-parse', 'HEAD')).toBe(before);
    expect(fs.existsSync(path.join(look(remote, branch), `change-sets/${pub.changeSet.id}.yaml`))).toBe(true);
    expect((await call(env, 'POST', '/api/git/export', { actor: 'shared.checker', changeSet: pub.changeSet.id })).body.status).toBe('unchanged');
  });

  it('a workflow carries its engine package, and an environment gets an activation', async () => {
    const env = freshEnv();
    const remote = initBareRepo(tmp());
    await call(env, 'PUT', '/api/git', { actor: 'shared.checker', remote, branch: 'main', target: 'dtfx', environment: 'uat' });
    const res = await apply(env, readBundle(wpsTrainingBundle(env)), { tenant: 'owner', bankTenant: 'example-bank', actor: 'shared.designer', bankActor: 'example.maker' });
    const exports: any[] = [];
    for (const set of res.changeSets) {
      const d = [...env.store.drafts('owner'), ...env.store.drafts('example-bank')].find((x) => x.key === set[0]);
      if (!d) continue;
      const pub = await call(env, 'POST', '/api/publish', { tenant: d.tenant, key: d.key, checker: d.tenant === 'owner' ? 'shared.checker' : 'example.checker' });
      expect(pub.status, JSON.stringify(pub.body).slice(0, 300)).toBe(200);
      exports.push(pub.body.git);
    }
    expect(exports.every((g) => g.status === 'committed'), JSON.stringify(exports.map((g) => g.error ?? g.status))).toBe(true);
    const d = look(remote);
    const dir = path.join(d, 'releases/uae.wpsx/uae.wpsx.wf.salary-file-out.v1/1.0.0/dtfx');
    expect(fs.existsSync(path.join(dir, 'manifest.yaml'))).toBe(true);
    expect(fs.readFileSync(path.join(dir, 'package.sha256'), 'utf8')).toMatch(/^sha256:[0-9a-f]{64}\n$/);
    const bank = fs.readdirSync(path.join(d, 'banks/example-bank'));
    expect(bank.length).toBeGreaterThan(0);
    const acts = fs.readdirSync(path.join(d, 'environments/uat/activations'));
    expect(acts.length).toBe(exports.length);
    const a = parseYaml(fs.readFileSync(path.join(d, 'environments/uat/activations', acts[0]), 'utf8'));
    expect(a).toMatchObject({ kind: 'activation', environment: 'uat' });
  }, 120000);
});
