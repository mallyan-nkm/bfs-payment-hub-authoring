import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { FakeGitHub } from '../../test/support/fake-github.js';
import type { ContentConnection } from './content-store.js';

// An in-memory GitHub for local development and for the commands that only read: the fake GitHub
// API, seeded from this repository's `content-configurations/`. It reads that folder once and holds
// everything in memory, so it is still no local copy: nothing the studio does is written to disk, and
// everything is gone when the process ends (R-11-24).

const CONTENT_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'content-configurations');

export const LOCAL_REPO = { owner: 'local', repo: 'content', branch: 'main' };

export async function startLocalContent(opts: { token?: string; dir?: string; unref?: boolean; port?: number } = {}) {
  const token = opts.token ?? 'local-development-token';
  const fake = new FakeGitHub(token);
  const url = await fake.start({ unref: opts.unref, port: opts.port });
  fake.seedFromDir(`${LOCAL_REPO.owner}/${LOCAL_REPO.repo}`, LOCAL_REPO.branch, opts.dir ?? CONTENT_DIR, 'content-configurations');
  const connection: ContentConnection = { api: url, owner: LOCAL_REPO.owner, repo: LOCAL_REPO.repo, branch: LOCAL_REPO.branch, root: 'content-configurations', token: () => token };
  return { fake, url, token, connection, stop: () => fake.stop() };
}
