// Shared ESLint 9 flat-config presets for the Talky client workspace.
//
// Usage from a package (`packages/<name>/eslint.config.js`):
//
//   import { talkyConfig } from '@talky/config/eslint';
//   export default talkyConfig({ tsconfigRootDir: import.meta.dirname, platformAgnostic: true });

import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import importX from 'eslint-plugin-import-x';
import globals from 'globals';
import tseslint from 'typescript-eslint';

/** Paths that are never linted (generated code, build output, caches). */
export const ignores = [
  '**/node_modules/**',
  '**/dist/**',
  '**/coverage/**',
  '**/.turbo/**',
  '**/src/gen/**',
];

/**
 * Packages that `@talky/core` must never import. `@talky/core` runs unchanged on the React web
 * app, the Tauri desktop shell and React Native, so anything platform-specific has to arrive
 * through the injected `Platform` / `TimerHost` / `WebSocketFactory` seams instead.
 */
export const forbiddenCoreImports = [
  { group: ['react', 'react/*'], message: 'packages/core is platform-agnostic: no React.' },
  {
    group: ['react-dom', 'react-dom/*'],
    message: 'packages/core is platform-agnostic: no React DOM.',
  },
  {
    group: ['react-native', 'react-native/*', 'expo', 'expo-*'],
    message: 'packages/core is platform-agnostic: no React Native.',
  },
  { group: ['@tauri-apps/*'], message: 'packages/core is platform-agnostic: no Tauri APIs.' },
  {
    group: ['next', 'next/*', 'vue', 'svelte', '@angular/*'],
    message: 'packages/core is platform-agnostic: no UI framework imports.',
  },
];

/** Browser/DOM globals `@talky/core` must not reach for. */
export const forbiddenCoreGlobals = [
  { name: 'window', message: 'Use the injected Platform/TimerHost seam instead of `window`.' },
  { name: 'document', message: 'packages/core never touches the DOM.' },
  { name: 'localStorage', message: 'Use the `Storage` interface instead of `localStorage`.' },
  { name: 'sessionStorage', message: 'Use the `Storage` interface instead of `sessionStorage`.' },
  { name: 'navigator', message: 'Pass platform details through `ClientInfo` / `Platform`.' },
  { name: 'location', message: 'Pass the WebSocket URL through `WsClientOptions.url`.' },
  { name: 'fetch', message: 'M0 core is WebSocket-only; HTTP arrives through an injected seam.' },
];

/**
 * Build the flat config for one package.
 *
 * @param {object} options
 * @param {string} options.tsconfigRootDir Absolute path of the package root (`import.meta.dirname`).
 * @param {boolean} [options.platformAgnostic] Apply the `@talky/core` boundary rules to `src/**`.
 * @param {boolean} [options.node] Allow Node globals (tooling, mock server, tests).
 * @param {string[]} [options.extraIgnores] Additional ignore globs.
 * @returns {import('typescript-eslint').ConfigArray}
 */
export function talkyConfig({
  tsconfigRootDir,
  platformAgnostic = false,
  node = false,
  extraIgnores = [],
} = {}) {
  return tseslint.config(
    { ignores: [...ignores, ...extraIgnores] },
    js.configs.recommended,
    ...tseslint.configs.strictTypeChecked,
    ...tseslint.configs.stylisticTypeChecked,
    {
      languageOptions: {
        parserOptions: { projectService: true, tsconfigRootDir },
        globals: node ? globals.node : {},
      },
      linterOptions: { reportUnusedDisableDirectives: 'error' },
      rules: {
        // Correctness first: an unhandled rejection in a realtime client loses messages.
        '@typescript-eslint/no-floating-promises': 'error',
        '@typescript-eslint/no-misused-promises': 'error',
        '@typescript-eslint/switch-exhaustiveness-check': 'error',
        // `any` is banned outright; `unknown` + narrowing is always available.
        '@typescript-eslint/no-explicit-any': 'error',
        '@typescript-eslint/no-non-null-assertion': 'error',
        '@typescript-eslint/consistent-type-imports': [
          'error',
          { prefer: 'type-imports', fixStyle: 'separate-type-imports' },
        ],
        '@typescript-eslint/no-unused-vars': [
          'error',
          { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrorsIgnorePattern: '^_' },
        ],
        'no-console': ['error', { allow: ['warn', 'error', 'info'] }],
        eqeqeq: ['error', 'always', { null: 'ignore' }],
      },
    },
    // Type-aware rules cannot run on plain JS config files.
    {
      files: ['**/*.js', '**/*.mjs', '**/*.cjs'],
      extends: [tseslint.configs.disableTypeChecked],
      languageOptions: { globals: globals.node },
    },
    ...(platformAgnostic
      ? [
          {
            files: ['src/**/*.ts'],
            plugins: { 'import-x': importX },
            rules: {
              'no-restricted-imports': ['error', { patterns: forbiddenCoreImports }],
              'no-restricted-globals': ['error', ...forbiddenCoreGlobals],
              // Node built-ins would break the React Native and browser targets.
              'import-x/no-nodejs-modules': 'error',
            },
          },
        ]
      : []),
    // Tests may use Node built-ins and loose assertions.
    {
      files: ['test/**/*.ts', '**/*.test.ts', 'scripts/**/*.{ts,js,mjs}'],
      languageOptions: { globals: globals.node },
      rules: {
        '@typescript-eslint/no-unsafe-assignment': 'off',
        '@typescript-eslint/no-unsafe-member-access': 'off',
        '@typescript-eslint/no-unsafe-argument': 'off',
        'no-console': 'off',
      },
    },
    prettier,
  );
}

export default talkyConfig;
