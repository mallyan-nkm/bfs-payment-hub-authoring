import { describe, expect, it } from 'vitest';
import { dispatch } from '../src/server/api.js';
import { toYaml } from '../src/core/yaml.js';
import { freshEnv, restart } from './helpers.js';
import { BRANCH, fake } from './support/content.js';

// A document that holds one object twice (here the same list as its reads and its provenance clauses) was
// written to its draft branch with YAML anchors and aliases, and published without them. The branch's file
// then differed from the published one, so after a restart a branch that still held other drafts listed the
// published release as a draft again.

const ROOT = 'content-configurations';
const call = (env: any, method: string, p: string, body: any = {}) => dispatch(env, { method, path: p, query: {}, body });

function act(n: string) {
  const twice = ['header'];
  return {
    kind: 'activity', name: `p.enrich.cs-alias-${n}.v1`, release: '1.0.0', title: 'Note', type: 'enrich', reads: twice, writes: ['facts.note'],
    outcomes: [{ name: 'done', primary: true }], effect: 'pure', implementation: { binding: 'builtin', service: 'noop' },
    provenance: { origin: 'authored', status: 'provisional', clauses: twice },
  };
}
const file = (n: string) => `${ROOT}/artefacts/p/p.enrich.cs-alias-${n}.v1/1.0.0.yaml`;

describe('documents that hold an object twice', () => {
  it('are written without aliases, so the draft and its published file are the same text', () => {
    const text = toYaml(act('t'));
    expect(text).not.toMatch(/[&*]a\d/);
    expect(text).toContain('clauses:\n    - header');
  });

  it('a published draft does not come back as a draft after a restart, while its branch keeps another draft', async () => {
    const env = freshEnv();
    const a = env.store.saveDraft('owner', 'shared.designer', act('a1')).key;
    const id = env.store.entries.get(a)!.changeSet!;
    const b = env.store.saveDraft('owner', 'shared.designer', act('a2'), { changeSet: id }).key;
    await env.store.flush();
    const repo = env.store.content!.repository;
    expect(fake().files(repo, `studio/${id}`)[file('a1')]).not.toMatch(/[&*]a\d/);
    const p = await call(env, 'POST', '/api/publish', { tenant: 'owner', key: a, checker: 'shared.checker' });
    expect(p.status, JSON.stringify(p.body).slice(0, 300)).toBe(200);
    // The branch keeps the other draft, and its copy of the published one is the published text.
    expect(fake().branches(repo)).toContain(`studio/${id}`);
    expect(fake().files(repo, `studio/${id}`)[file('a1')]).toBe(fake().files(repo, BRANCH)[file('a1')]);
    const again = await restart(env);
    expect(again.store.drafts('owner').map((d) => d.key)).toEqual([b]);
    expect(again.store.get('p.enrich.cs-alias-a1.v1', '1.0.0')?.status).toBe('published');
  });

  it('a branch an older studio wrote with aliases does not list the release it published as a draft', async () => {
    const env = freshEnv();
    const a = env.store.saveDraft('owner', 'shared.designer', act('o1')).key;
    const id = env.store.entries.get(a)!.changeSet!;
    const b = env.store.saveDraft('owner', 'shared.designer', act('o2'), { changeSet: id }).key;
    await env.store.flush();
    const p = await call(env, 'POST', '/api/publish', { tenant: 'owner', key: a, checker: 'shared.checker' });
    expect(p.status).toBe(200);
    // What an older studio left on the branch: the same document, written with an anchor and an alias.
    const repo = env.store.content!.repository;
    const aliased = fake().files(repo, BRANCH)[file('o1')].replace('reads:\n  - header', 'reads: &a1\n  - header').replace('clauses:\n    - header', 'clauses: *a1');
    expect(aliased).toContain('clauses: *a1');
    fake().commitFiles(repo, `studio/${id}`, { [file('o1')]: aliased }, 'Draft written by an older studio');
    const again = await restart(env);
    expect(again.store.drafts('owner').map((d) => d.key)).toEqual([b]);
  });
});
