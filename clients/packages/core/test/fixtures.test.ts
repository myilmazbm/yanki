import { findFixturesDir, helloProfile, loadWsFixtures, replayFixture } from '@yanki/test-utils';
import type { WsFixture } from '@yanki/test-utils';
import { describe, expect, it } from 'vitest';
import type { ClientPlatform, WsClientOptions } from '../src/index.js';
import { createHarness } from './helpers.js';

/**
 * Golden-fixture runner.
 *
 * `api/fixtures/ws/*.json` is authored by the server work package and executed by both sides:
 * the server replays `c2s` and asserts `s2c`, this suite does the mirror image. Fixtures marked
 * `clientReplay: false` describe client-side protocol violations a conforming client cannot
 * produce; they are listed as skipped so the coverage gap stays visible.
 */
const fixturesDir = findFixturesDir();
const fixtures: WsFixture[] = loadWsFixtures(fixturesDir);

const PLATFORMS: ReadonlySet<string> = new Set<ClientPlatform>([
  'web',
  'desktop',
  'ios',
  'android',
]);

function clientOptionsFor(fixture: WsFixture): Partial<WsClientOptions> {
  const profile = helloProfile(fixture);
  const options: Partial<WsClientOptions> = { random: () => 0.5 };
  const token = profile.token;
  if (token !== undefined) options.getToken = () => Promise.resolve(token);
  if (profile.deviceId !== undefined) options.deviceId = profile.deviceId;
  const client = profile.client;
  if (client !== undefined && PLATFORMS.has(client.platform)) {
    options.client = { ...client, platform: client.platform as ClientPlatform };
  }
  return options;
}

async function runFixture(fixture: WsFixture): Promise<void> {
  // `autoWelcome` off: every server frame comes from the fixture, nothing is improvised.
  const harness = createHarness({
    server: { autoWelcome: false, autoPong: false },
    client: clientOptionsFor(fixture),
  });
  try {
    const result = await replayFixture(fixture, {
      server: harness.server,
      clock: harness.clock,
      start: () => {
        harness.client.connect();
      },
      triggers: {
        bye: () => {
          harness.client.close();
        },
      },
      getState: () => harness.client.state,
    });
    expect(result.matched).toBeGreaterThan(0);
  } finally {
    await harness.dispose();
  }
}

describe('golden fixtures (api/fixtures/ws)', () => {
  it('discovers the fixture directory', () => {
    expect(fixturesDir).not.toBeNull();
    expect(fixtures.length).toBeGreaterThan(0);
  });

  for (const fixture of fixtures) {
    const title = `replays ${fixture.name}${fixture.description === undefined ? '' : ` — ${fixture.description}`}`;
    if (!fixture.clientReplay) {
      it.skip(`${title} (server-only sequence)`, () => undefined);
      continue;
    }
    it(title, async () => {
      await runFixture(fixture);
    });
  }
});
