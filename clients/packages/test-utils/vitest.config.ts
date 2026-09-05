import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    name: 'test-utils',
    environment: 'node',
    include: ['test/**/*.test.ts'],
    testTimeout: 20_000,
  },
});
