import { defineConfig } from 'vitest/config';

// Black-box suites: the studio as separate processes, reached over HTTP, the CLI and a browser.
export default defineConfig({
  test: {
    include: ['test/blackbox/**/*.test.ts'],
    testTimeout: 300000,
    hookTimeout: 120000,
    pool: 'forks',
    fileParallelism: false,
  },
});
