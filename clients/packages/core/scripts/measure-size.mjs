// Bundle-size dry run for @talky/core.
//
//   pnpm --filter @talky/core run size
//
// Two numbers matter:
//   * "core only"  — what @talky/core itself adds to an app that already ships protobuf-es.
//                    This is the number the < 15 KB gz budget applies to.
//   * "with deps"  — the whole subtree, for an app that has no other protobuf-es user.
//
// A third number checks tree-shaking: an app that only imports `createWsClient` must not drag
// in `MemoryStorage`, the deduper or the codec helpers it never calls.

import { brotliCompressSync, constants, gzipSync } from 'node:zlib';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const EXTERNALS = [
  '@bufbuild/protobuf',
  '@bufbuild/protobuf/*',
  '@talky/protocol',
  '@talky/protocol/*',
];

/** @param {{ entry: string, external: string[] }} options */
async function measure({ entry, external }) {
  const result = await build({
    entryPoints: [entry],
    bundle: true,
    minify: true,
    format: 'esm',
    platform: 'neutral',
    target: 'es2022',
    external,
    write: false,
    legalComments: 'none',
  });
  const output = result.outputFiles[0];
  if (output === undefined) throw new Error('esbuild produced no output');
  const raw = Buffer.from(output.contents);
  return {
    raw: raw.byteLength,
    gzip: gzipSync(raw, { level: 9 }).byteLength,
    brotli: brotliCompressSync(raw, {
      params: { [constants.BROTLI_PARAM_QUALITY]: 11 },
    }).byteLength,
  };
}

const kb = (bytes) => `${(bytes / 1024).toFixed(2)} kB`;

const scratch = mkdtempSync(join(tmpdir(), 'talky-core-size-'));
try {
  const clientOnlyEntry = join(scratch, 'client-only.ts');
  writeFileSync(
    clientOnlyEntry,
    `export { createWsClient } from ${JSON.stringify(join(packageRoot, 'src/ws/client.ts').replace(/\\/g, '/'))};\n`,
    'utf8',
  );

  const rows = [
    [
      'full public API, core only',
      await measure({ entry: join(packageRoot, 'src/index.ts'), external: EXTERNALS }),
    ],
    [
      'full public API, with deps',
      await measure({ entry: join(packageRoot, 'src/index.ts'), external: [] }),
    ],
    [
      'createWsClient only, core only',
      await measure({ entry: clientOnlyEntry, external: EXTERNALS }),
    ],
  ];

  const budgetGzip = 15 * 1024;
  console.info('\n@talky/core bundle size (esbuild --bundle --minify, esm/es2022)\n');
  console.info(
    ['entry'.padEnd(32), 'raw'.padStart(11), 'gzip'.padStart(11), 'brotli'.padStart(11)].join(''),
  );
  console.info('-'.repeat(65));
  for (const [label, size] of rows) {
    console.info(
      [
        label.padEnd(32),
        kb(size.raw).padStart(11),
        kb(size.gzip).padStart(11),
        kb(size.brotli).padStart(11),
      ].join(''),
    );
  }

  const headline = rows[0][1];
  const verdict = headline.gzip <= budgetGzip ? 'within' : 'OVER';
  console.info(
    `\nBudget: ${kb(budgetGzip)} gzip for "core only" — ${verdict} budget (${kb(headline.gzip)}).\n`,
  );
  if (headline.gzip > budgetGzip) process.exitCode = 1;
} finally {
  rmSync(scratch, { recursive: true, force: true });
}
