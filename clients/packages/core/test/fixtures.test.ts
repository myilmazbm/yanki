import { findFixturesDir, loadWsFixtures, replayFixture } from '@talky/test-utils';
import type { WsFixture } from '@talky/test-utils';
import { describe, expect, it } from 'vitest';
import { createHarness } from './helpers.js';

/**
 * Golden-fixture runner.
 *
 * `api/fixtures/ws/*.json` is authored by the server work package and executed by both sides:
 * the server replays `c2s` and asserts `s2c`, this suite does the mirror image. Until those
 * files land the suite reports that fact explicitly instead of silently passing on nothing.
 */
const fixturesDir = findFixturesDir();
const fixtures: WsFixture[] = loadWsFixtures(fixturesDir);

async function runFixture(fixture: WsFixture): Promise<void> {
  // `autoWelcome` off: every server frame comes from the fixture, nothing is improvised.
  const harness = createHarness({
    server: { autoWelcome: false, autoPong: false },
    client: { random: () => 0.5 },
  });
  try {
    const result = await replayFixture(fixture, {
      server: harness.server,
      clock: harness.clock,
      start: () => {
        harness.client.connect();
      },
    });
    expect(result.matched).toBeGreaterThan(0);
  } finally {
    await harness.dispose();
  }
}

describe('golden fixtures (api/fixtures/ws)', () => {
  if (fixtures.length === 0) {
    it('reports that no golden fixtures are present yet', () => {
      const where = fixturesDir ?? '<api/fixtures/ws not found>';
      console.info(
        `[fixtures] no golden fixtures found in ${where} — nothing to replay. ` +
          'The server work package authors these files; this suite starts enforcing them ' +
          'automatically as soon as one lands.',
      );
      expect(fixtures).toEqual([]);
    });
    return;
  }

  it('discovers the fixture directory', () => {
    expect(fixturesDir).not.toBeNull();
    expect(fixtures.length).toBeGreaterThan(0);
  });

  for (const fixture of fixtures) {
    it(`replays ${fixture.name}${fixture.description === undefined ? '' : ` — ${fixture.description}`}`, async () => {
      await runFixture(fixture);
    });
  }
});
