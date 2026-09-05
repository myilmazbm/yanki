/**
 * Shared Prettier options for the Talky client workspace.
 *
 * @type {import('prettier').Config}
 */
const config = {
  singleQuote: true,
  printWidth: 100,
  trailingComma: 'all',
  semi: true,
  tabWidth: 2,
  useTabs: false,
  arrowParens: 'always',
  bracketSpacing: true,
  endOfLine: 'lf',
  overrides: [
    // JSON has no trailing commas; tsconfig files keep their comments (jsonc) but must stay
    // byte-compatible with plain JSON tooling.
    {
      files: ['*.json', '*.jsonc', '.prettierrc'],
      options: { trailingComma: 'none', printWidth: 120 },
    },
  ],
};

export default config;
