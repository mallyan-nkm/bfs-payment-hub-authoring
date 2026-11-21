// A studio environment over its own fork of the seeded content repository in the fake GitHub
// (test/support/content.ts): an empty, private branch space, in memory only.
export { freshEnv, restart, fake, repoOf } from './support/content.js';

export function clone<T>(x: T): T {
  return JSON.parse(JSON.stringify(x));
}
