// Root flat config. Each package also has its own `eslint.config.js` so that
// `turbo run lint` can lint packages independently (and cache them independently);
// this root config exists for editor integration and for `pnpm exec eslint .` sweeps.
import { talkyConfig } from '@talky/config/eslint';

export default talkyConfig({
  tsconfigRootDir: import.meta.dirname,
  node: true,
  extraIgnores: ['packages/*/**'],
});
