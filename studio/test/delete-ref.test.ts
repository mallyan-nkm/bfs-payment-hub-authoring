import { describe, expect, it } from 'vitest';
import { dispatch } from '../src/server/api.js';
import { freshEnv, restart } from './helpers.js';
import { BRANCH, fake } from './support/content.js';

// GitHub's delete-ref takes no expected SHA (Issue I-61). The studio deletes a change set branch it has
// published only if, read again right before the delete, the branch is where the studio last saw it. A push
// to the branch before that read keeps the branch, with what was pushed, and the result names it.

const ROOT = 'content-configurations';
const call = (env: any, method: string, p: string, body: any = {}) => dispatch(env, { method, path: p, query: {}, body });
const act = (n: string) => ({
  kind: 'activity', name: `p.enrich.cs-del-${n}.v1`, release: '1.0.0', title: 'Note', type: 'enrich', reads: ['header'], writes: ['facts.note'],
  outcomes: [{ name: 'done', primary: true }], effect: 'pure', implementation: { binding: 'builtin', service: 'noop' },
  provenance: { origin: 'authored', status: 'provisional', clauses: ['Delete-ref test'] },
});

describe('deleting a published change set branch (I-61)', () => {
  it('keeps a branch someone pushed to after the studio saw it, names it, and keeps what was pushed', async () => {
    const env = freshEnv();
    const key = env.store.saveDraft('owner', 'shared.designer', act('a')).key;
    await env.store.flush();
    const id = env.store.entries.get(key)!.changeSet!;
    const repo = env.store.content!.repository;
    const other = `${ROOT}/artefacts/p/p.enrich.cs-del-b.v1/1.0.0.yaml`;
    let pushed: string | undefined;
    // Another writer pushes to the change set branch while the studio publishes it.
    fake().beforeRefUpdate = (full, branch) => {
      if (full !== repo || branch !== BRANCH || pushed) return;
      pushed = fake().commitFiles(repo, `studio/${id}`, { [other]: 'kind: activity\nname: p.enrich.cs-del-b.v1\nrelease: 1.0.0\n' }, 'Pushed by someone else');
    };
    try {
      const p = await call(env, 'POST', '/api/publish', { tenant: 'owner', key, checker: 'shared.checker' });
      expect(p.status, JSON.stringify(p.body).slice(0, 300)).toBe(200);
      expect(pushed).toBeDefined();
      expect(p.body.git.deletedBranches).toBeUndefined();
      expect(p.body.git.keptBranches).toEqual([expect.objectContaining({ branch: `studio/${id}`, found: pushed, reason: expect.stringMatching(/moved .* since the studio saw it; it is kept/) })]);
    } finally {
      fake().beforeRefUpdate = undefined;
    }
    expect(fake().branches(repo)).toContain(`studio/${id}`);
    expect(fake().deleted).not.toContain(`${repo} studio/${id}`);
    expect(fake().files(repo, `studio/${id}`)[other]).toMatch(/cs-del-b/);
    // The git page names it too.
    expect((await call(env, 'GET', '/api/git')).body.keptBranches.map((k: any) => k.branch)).toEqual([`studio/${id}`]);
    // The published release is on the content branch; what was pushed waits on its branch.
    const again = await restart(env);
    expect(again.store.get('p.enrich.cs-del-a.v1', '1.0.0')?.status).toBe('published');
    expect(fake().branches(repo)).toContain(`studio/${id}`);
  });

  it('deletes the branch when nobody moved it', async () => {
    const env = freshEnv();
    const key = env.store.saveDraft('owner', 'shared.designer', act('c')).key;
    await env.store.flush();
    const id = env.store.entries.get(key)!.changeSet!;
    const p = await call(env, 'POST', '/api/publish', { tenant: 'owner', key, checker: 'shared.checker' });
    expect(p.body.git.deletedBranches).toEqual([`studio/${id}`]);
    expect(p.body.git.keptBranches).toBeUndefined();
  });
});
