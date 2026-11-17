import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    include: ['test/**/*.test.ts'],
    exclude: ['test/blackbox/**', 'node_modules/**'],
    testTimeout: 120000,
    hookTimeout: 120000,
    pool: 'forks',
  },
});
