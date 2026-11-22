import { defineConfig } from 'vitest/config';

// Black-box suites: the studio as separate processes, reached over HTTP, the CLI and a browser.
export default defineConfig({
  test: {
    include: ['test/blackbox/**/*.test.ts'],
    // A signing key made for the run: the studio signs what it releases, the deploy runner and the runtime verify it.
    setupFiles: ['test/support/signing-env.ts'],
    testTimeout: 300000,
    hookTimeout: 120000,
    pool: 'forks',
    fileParallelism: false,
  },
});
