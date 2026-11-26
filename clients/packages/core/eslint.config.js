import { yankiConfig } from '@yanki/config/eslint';

export default yankiConfig({
  tsconfigRootDir: import.meta.dirname,
  platformAgnostic: true,
  node: true,
});
