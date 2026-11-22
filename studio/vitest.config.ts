import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    include: ['test/**/*.test.ts'],
    exclude: ['test/blackbox/**', 'node_modules/**'],
    // The fake GitHub, seeded from content-configurations/: every test reads and writes it (R-11-21).
    setupFiles: ['test/support/signing-env.ts', 'test/support/setup.ts'],
    coverage: {
      provider: 'v8',
      include: ['src/**/*.ts'],
      exclude: ['src/server/main.ts', 'src/cli.ts', 'src/worker/main.ts', 'src/**/*.d.ts'],
      reporter: ['text-summary', 'json-summary', 'text'],
      reportsDirectory: 'coverage',
    },
    testTimeout: 120000,
    hookTimeout: 120000,
    pool: 'forks',
  },
});
