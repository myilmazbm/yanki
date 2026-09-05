import { defineConfig } from 'vitest/config';

/**
 * Workspace-level Vitest projects setup. `pnpm test` goes through Turborepo so that each
 * package is cached independently; `pnpm test:watch` uses this file to watch every project
 * in one process.
 */
export default defineConfig({
  test: {
    projects: ['packages/*'],
  },
});
