import { describe, expect, it } from 'vitest';
import { ContentStore, connectionFromEnv, type ContentConnection } from '../src/content/content-store.js';
import { TOKEN_ENV } from '../src/content/github.js';
import { seedContent } from '../src/content/seed.js';
import { envFor } from '../src/env.js';
import { dispatch } from '../src/server/api.js';

// The smoke test against real GitHub (R-09-19 to R-09-22, R-11-21 to R-11-27). Skipped unless
// TH_REAL_GITHUB=1. It needs TH_CONTENT_REPO=<owner/repo> and a scratch content branch of its own,
// TH_CONTENT_BRANCH (for example th-smoke-2026-11-22). TH_GITHUB_TOKEN is sent as given and may be a
// placeholder where a proxy authenticates the requests; it is never printed.
//
// It seeds the scratch branch, saves a draft, approves it in commit mode, then opens a pull request in
// pull-request mode, and at the end deletes only the branches it created. It refuses to run on main or
// master, on the repository's default branch, and on a branch with a commit it did not create. It
// writes to no other branch. An empty repository gets its first commit through the Contents API, on
// the scratch branch, since the Git Data API refuses an empty repository.

const RUN = process.env.TH_REAL_GITHUB === '1';
const MARK = 'th-smoke: start';
const AUTHOR = 'TransactHub Studio';

const ACT = (n: string) => ({
  kind: 'activity', name: `p.enrich.smoke-${n}.v1`, release: '1.0.0', title: 'Smoke test note', type: 'enrich', reads: ['header'], writes: ['facts.note'],
  outcomes: [{ name: 'done', primary: true }], effect: 'pure', implementation: { binding: 'builtin', service: 'noop' },
  provenance: { origin: 'authored', status: 'provisional', clauses: ['Real GitHub smoke test'] },
});

/** A small client for the test's own set-up and clean-up; the studio's client is the one under test. */
function gh(conn: ContentConnection) {
  const token = () => process.env[TOKEN_ENV] || undefined;
  const scrub = (t: string) => (token() ? t.split(token()!).join('***') : t);
  const base = `${conn.api.replace(/\/+$/, '')}/repos/${conn.owner}/${conn.repo}`;
  return async (method: string, p: string, body?: any): Promise<{ status: number; data: any }> => {
    const t = token();
    const res = await fetch(base + p, {
      method,
      headers: { accept: 'application/vnd.github+json', 'x-github-api-version': '2022-11-28', 'user-agent': 'transacthub-studio-smoke', ...(t ? { authorization: `Bearer ${t}` } : {}), ...(body ? { 'content-type': 'application/json' } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    let data: any;
    try {
      data = text ? JSON.parse(text) : undefined;
    } catch {
      data = { message: scrub(text.slice(0, 200)) };
    }
    if (data?.message) data.message = scrub(String(data.message));
    return { status: res.status, data };
  };
}

/** Every commit of a branch, following all parents. */
async function history(call: ReturnType<typeof gh>, head: string) {
  const out: { sha: string; author: string; message: string; parents: string[] }[] = [];
  const seen = new Set<string>();
  const queue = [head];
  while (queue.length) {
    const sha = queue.shift()!;
    if (seen.has(sha)) continue;
    seen.add(sha);
    const c = await call('GET', `/git/commits/${sha}`);
    if (c.status !== 200) throw new Error(`Cannot read commit ${sha}: ${c.status} ${c.data?.message ?? ''}`);
    out.push({ sha, author: c.data.author?.name, message: c.data.message, parents: c.data.parents.map((p: any) => p.sha) });
    queue.push(...c.data.parents.map((p: any) => p.sha));
    if (out.length > 200) throw new Error('The branch has more than 200 commits: it is not a scratch branch');
  }
  return out;
}

describe.skipIf(!RUN)('smoke test against real GitHub (TH_REAL_GITHUB=1)', () => {
  it('seeds a scratch branch, drafts, approves in commit mode, opens a pull request, and deletes only what it created', async () => {
    const conn = connectionFromEnv();
    if (!conn) throw new Error('Set TH_CONTENT_REPO=<owner/repo> and TH_CONTENT_BRANCH=<a scratch branch>');
    const branch = conn.branch;
    if (!process.env.TH_CONTENT_BRANCH || /^(main|master)$/i.test(branch)) throw new Error(`Refused: the smoke test runs only on a scratch content branch named in TH_CONTENT_BRANCH, never ${branch}`);
    const call = gh(conn);
    const repo = await call('GET', '');
    if (repo.status !== 200) throw new Error(`Cannot read ${conn.owner}/${conn.repo}: ${repo.status} ${repo.data?.message ?? ''}`);
    if (repo.data.default_branch === branch) throw new Error(`Refused: ${branch} is the repository's default branch`);
    if (repo.data.permissions && !repo.data.permissions.push) throw new Error(`Refused: no push permission on ${conn.owner}/${conn.repo}`);

    // The studio branches that were there before: never touched.
    const refsBefore = await call('GET', '/git/matching-refs/heads/studio');
    const before = new Map<string, string>(refsBefore.status === 200 ? refsBefore.data.map((r: any) => [r.ref, r.object.sha]) : []);
    const created = new Set<string>();

    // The scratch content branch: new, or one this test made before (every commit by the studio, from its own start).
    const existing = await call('GET', `/git/ref/heads/${branch}`);
    if (existing.status === 200) {
      const commits = await history(call, existing.data.object.sha);
      const roots = commits.filter((c) => !c.parents.length);
      const foreign = commits.filter((c) => c.author !== AUTHOR);
      if (foreign.length || roots.length !== 1 || !roots[0].message.startsWith(MARK)) {
        throw new Error(`Refused: ${branch} already has commits the smoke test did not create (${foreign.length} by others${roots[0]?.message.startsWith(MARK) ? '' : '; it does not start from the smoke test\'s own first commit'}). Choose a new TH_CONTENT_BRANCH.`);
      }
    } else if (existing.status === 409) {
      // An empty repository: the Git Data API refuses it, so the first commit goes through the Contents API, on the scratch branch.
      const put = await call('PUT', '/contents/content-configurations/README.md', {
        message: `${MARK} ${branch}`, branch, content: Buffer.from('# Smoke test content\n\nA scratch branch of the studio smoke test.\n').toString('base64'),
        author: { name: AUTHOR, email: 'studio@transacthub.invalid' }, committer: { name: AUTHOR, email: 'studio@transacthub.invalid' },
      });
      if (put.status !== 201 && put.status !== 200) throw new Error(`The repository is empty and its first commit could not be made on ${branch}: ${put.status} ${put.data?.message ?? ''}`);
      if ((await call('GET', `/git/ref/heads/${branch}`)).status !== 200) throw new Error(`The repository was empty: GitHub put the first commit on another branch, not ${branch}. Nothing more was written.`);
      created.add(branch);
    } else {
      // An orphan first commit: the scratch branch shares no history with the repository's other branches.
      const blob = await call('POST', '/git/blobs', { content: Buffer.from('# Smoke test content\n').toString('base64'), encoding: 'base64' });
      const tree = await call('POST', '/git/trees', { tree: [{ path: 'content-configurations/README.md', mode: '100644', type: 'blob', sha: blob.data.sha }] });
      const who = { name: AUTHOR, email: 'studio@transacthub.invalid', date: new Date().toISOString() };
      const commit = await call('POST', '/git/commits', { message: `${MARK} ${branch}`, tree: tree.data.sha, parents: [], author: who, committer: who });
      const ref = await call('POST', '/git/refs', { ref: `refs/heads/${branch}`, sha: commit.data.sha });
      if (ref.status !== 201) throw new Error(`Could not create ${branch}: ${ref.status} ${ref.data?.message ?? ''}`);
      created.add(branch);
    }

    const studio = (c: ContentStore) => c.writes.filter((w) => w.branch.startsWith('studio/')).map((w) => w.branch);
    let store: ContentStore | undefined;
    try {
      store = await ContentStore.open(conn);
      const env = envFor(store, { banks: [] });
      const seed = await seedContent(env);
      expect(['committed', 'unchanged']).toContain(seed.status);
      expect(env.store.get('p.wf.commercial.v1')?.status).toBe('published');
      const api = (method: string, p: string, body: any = {}) => dispatch(env, { method, path: p, query: {}, body });
      const stamp = Date.now().toString(36);

      // Commit mode: a draft on its own branch, approved, published as one commit; its branch is deleted.
      const d = await api('POST', '/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: ACT(`c${stamp}`) });
      expect(d.status, JSON.stringify(d.body).slice(0, 300)).toBe(200);
      const id = env.store.entries.get(d.body.draft.key)!.changeSet!;
      created.add(`studio/${id}`);
      const p = await api('POST', '/api/publish', { tenant: 'owner', key: d.body.draft.key, checker: 'shared.checker' });
      expect(p.status, JSON.stringify(p.body).slice(0, 300)).toBe(200);
      expect(p.body.git).toMatchObject({ status: 'committed', branch });
      expect((await call('GET', `/git/ref/heads/studio/${id}`)).status).toBe(404);

      // Pull-request mode: approval opens a pull request from the change set's branch.
      expect((await api('PUT', '/api/git', { actor: 'shared.checker', mode: 'pull-request' })).status).toBe(200);
      const d2 = await api('POST', '/api/drafts', { tenant: 'owner', actor: 'shared.designer', doc: ACT(`p${stamp}`) });
      expect(d2.status).toBe(200);
      const id2 = env.store.entries.get(d2.body.draft.key)!.changeSet!;
      created.add(`studio/${id2}`);
      const p2 = await api('POST', '/api/publish', { tenant: 'owner', key: d2.body.draft.key, checker: 'shared.checker' });
      expect(p2.status, JSON.stringify(p2.body).slice(0, 300)).toBe(200);
      expect(p2.body.git).toMatchObject({ status: 'pull-request', branch: `studio/${id2}` });
      expect(p2.body.git.pullRequest.url).toMatch(/\/pull\/\d+$/);

      // A restart reads it all back.
      const again = envFor(await ContentStore.open(conn), { banks: [] });
      expect(again.store.get(`p.enrich.smoke-c${stamp}.v1`, '1.0.0')?.status).toBe('published');
      expect(again.store.entries.get(`p.enrich.smoke-p${stamp}.v1@1.0.0#draft`)?.awaitingMerge).toBe(`studio/${id2}`);
      expect(await again.store.verifyAudit()).toEqual({ ok: true });
    } finally {
      // Clean up: only the branches this run created. Deleting a pull request's head branch closes it.
      for (const b of store ? studio(store) : []) created.add(b);
      for (const b of [...created].filter((x) => x.startsWith('studio/')).concat([...created].filter((x) => !x.startsWith('studio/')))) {
        if (/^(main|master)$/i.test(b) || b === repo.data.default_branch || before.has(`refs/heads/${b}`)) continue;
        await call('DELETE', `/git/refs/heads/${b}`);
      }
    }
    // Every studio branch that was there before is untouched.
    const refsAfter = await call('GET', '/git/matching-refs/heads/studio');
    const after = new Map<string, string>(refsAfter.status === 200 ? refsAfter.data.map((r: any) => [r.ref, r.object.sha]) : []);
    for (const [r, sha] of before) expect(after.get(r), r).toBe(sha);
    for (const b of created) expect((await call('GET', `/git/ref/heads/${b}`)).status, b).toBe(404);
  }, 15 * 60_000);
});
