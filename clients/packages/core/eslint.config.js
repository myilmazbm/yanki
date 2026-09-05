import { talkyConfig } from '@talky/config/eslint';

export default talkyConfig({
  tsconfigRootDir: import.meta.dirname,
  platformAgnostic: true,
  node: true,
});
