import { ContentStore, type ContentConnection } from '../../src/content/content-store.js';
import { CONTENT_DIR, envFor, type StudioEnv } from '../../src/env.js';
import { FakeGitHub } from './fake-github.js';

// The content every test runs on: the fake GitHub, seeded from this repository's
// content-configurations/ (which holds the seeded factory and example bank, R-11-25). Each studio a test
// opens gets its own repository, a fork of the seeded one, so tests never see one another's writes.

export const TEST_TOKEN = 'synthetic-test-token';
export const SEED_REPO = 'seed/content';
export const BRANCH = 'main';

let state: { fake: FakeGitHub; base: ContentStore } | undefined;
let pending: Promise<{ fake: FakeGitHub; base: ContentStore }> | undefined;
let n = 0;

export function connection(fake: FakeGitHub, full: string, token = TEST_TOKEN): ContentConnection {
  const [owner, repo] = full.split('/');
  return { api: fake.url, owner, repo, branch: BRANCH, root: 'content-configurations', token: () => token };
}

/** The seeded fake, started once per test file. */
export async function seeded() {
  if (state) return state;
  pending ??= (async () => {
    const f = new FakeGitHub(TEST_TOKEN);
    await f.start({ unref: true });
    f.seedFromDir(SEED_REPO, BRANCH, CONTENT_DIR, 'content-configurations');
    const base = await ContentStore.open(connection(f, SEED_REPO));
    state = { fake: f, base };
    return state;
  })();
  return pending;
}

export function fake(): FakeGitHub {
  if (!state) throw new Error('The fake GitHub is not started: test/support/setup.ts starts it');
  return state.fake;
}

/** A new repository, a fork of the seeded one. */
export function forkRepo(): string {
  if (!state) throw new Error('The fake GitHub is not started: test/support/setup.ts starts it');
  const full = `test/env-${process.pid}-${++n}`;
  state.fake.fork(SEED_REPO, full);
  return full;
}

/** A studio over its own fork of the seeded repository: no request is made until it writes. */
export function freshEnv(opts: { banks?: string[] } = {}): StudioEnv {
  if (!state) throw new Error('The fake GitHub is not started: test/support/setup.ts starts it');
  const full = forkRepo();
  return envFor(state.base.forkInto(connection(state.fake, full)), { banks: opts.banks ?? [] });
}

/** The same studio started again: everything it knows is read from the fake GitHub. */
export async function restart(env: StudioEnv, opts: { banks?: string[] } = {}): Promise<StudioEnv> {
  await env.store.flush();
  return envFor(await ContentStore.open(env.store.content!.conn), { banks: opts.banks ?? [...env.store.bankPacks.values()].filter((b) => b !== 'example-bank') });
}

/** Where a studio's content lives in the fake: its repository's name. */
export const repoOf = (env: StudioEnv) => env.store.content!.repository;
