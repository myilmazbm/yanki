import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    name: 'core',
    environment: 'node',
    include: ['test/**/*.test.ts'],
    // The chaos test drives 200 connect/drop cycles through a virtual clock.
    testTimeout: 30_000,
    hookTimeout: 30_000,
  },
});
