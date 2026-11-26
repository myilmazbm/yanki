import { yankiConfig } from '@yanki/config/eslint';

export default yankiConfig({
  tsconfigRootDir: import.meta.dirname,
  node: true,
});
