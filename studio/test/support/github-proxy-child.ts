import { GitHubClient } from '../../src/content/github.js';

// A process of its own for test/github-proxy.test.ts: Node reads NODE_EXTRA_CA_CERTS only as a process
// starts. Reads one ref through the client with the environment it was given and prints what came back.
const c = new GitHubClient({ api: process.argv[2], owner: 'o', repo: 'r', token: () => 'synthetic-test-token' });
c.getRef('main').then(
  (sha) => console.log(`SHA ${sha}`),
  (e) => console.log(`ERR ${e.message}`),
);
