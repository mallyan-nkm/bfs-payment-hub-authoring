import { defineConfig } from 'vitest/config';

// End to end: the console in a real browser (Playwright's Chromium) against the console's API, the native host and a WPS
// file run on the runtime's DTFx build, with the store on PostgreSQL in a container.
export default defineConfig({
  test: {
    include: ['test/e2e/**/*.test.ts'],
    testTimeout: 900000,
    hookTimeout: 900000,
    pool: 'forks',
    fileParallelism: false,
  },
});
