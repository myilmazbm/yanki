import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { loadWsFixtures, replayFixture } from '@yanki/test-utils';
import type { WsFixture } from '@yanki/test-utils';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { createHarness } from './helpers.js';

/**
 * Self-test for the golden-fixture machinery.
 *
 * `fixtures.test.ts` runs whatever `api/fixtures/ws` contains — which is nothing until the
 * server work package lands its files. This suite writes fixtures of its own in the documented
 * format so the loader, the structural matcher and the replayer are covered from day one.
 */

const HANDSHAKE_FIXTURE = {
  description: 'Successful handshake followed by a ping round-trip',
  frames: [
    {
      dir: 'c2s',
      json: {
        rid: '…',
        hello: {
          protoVersion: 1,
          token: 'dev:u1',
          deviceId: '…',
          client: { platform: 'web', locale: 'tr-TR' },
        },
      },
    },
    {
      dir: 's2c',
      json: {
        id: '0190f0a1-0000-7000-8000-000000000002',
        welcome: { sessionId: 's1', userId: 'u1', heartbeatIntervalMs: 25000 },
      },
    },
    { dir: 'c2s', json: { ping: { ts: '…' } } },
    { dir: 's2c', json: { id: '…', pong: { ts: '1725540025000' } } },
  ],
};

const RESUME_FIXTURE = {
  description: 'Drop mid-session, resume with the since cursor',
  frames: [
    { dir: 'c2s', json: { rid: '…', hello: { protoVersion: 1, since: { c1: '7' } } } },
    { dir: 's2c', json: { id: '…', welcome: { sessionId: 's1', heartbeatIntervalMs: 25000 } } },
    { dir: 's2c', close: { code: 1001, reason: 'going away' } },
    { dir: 'c2s', json: { rid: '…', hello: { protoVersion: 1, since: { c1: '7' } } } },
    { dir: 's2c', json: { id: '…', welcome: { sessionId: 's2', heartbeatIntervalMs: 25000 } } },
  ],
};

let dir: string;

beforeAll(() => {
  dir = mkdtempSync(join(tmpdir(), 'yanki-ws-fixtures-'));
  writeFileSync(join(dir, 'handshake.json'), JSON.stringify(HANDSHAKE_FIXTURE, null, 2), 'utf8');
  writeFileSync(join(dir, 'resume.json'), JSON.stringify(RESUME_FIXTURE, null, 2), 'utf8');
  writeFileSync(join(dir, 'notes.txt'), 'ignored', 'utf8');
});

afterAll(() => {
  rmSync(dir, { recursive: true, force: true });
});

async function replay(fixture: WsFixture, since: Record<string, bigint> = {}): Promise<number> {
  const harness = createHarness({
    server: { autoWelcome: false, autoPong: false },
    client: { random: () => 0.5, getSince: () => since },
  });
  try {
    const result = await replayFixture(fixture, {
      server: harness.server,
      clock: harness.clock,
      start: () => {
        harness.client.connect();
      },
    });
    return result.matched;
  } finally {
    await harness.dispose();
  }
}

describe('fixture replayer', () => {
  it('loads only .json files, sorted, with descriptions', () => {
    const fixtures = loadWsFixtures(dir);
    expect(fixtures.map((fixture) => fixture.name)).toEqual(['handshake', 'resume']);
    expect(fixtures[0]?.description).toBe('Successful handshake followed by a ping round-trip');
    expect(fixtures[0]?.frames).toHaveLength(4);
  });

  it('returns an empty list for a missing directory', () => {
    expect(loadWsFixtures(join(dir, 'nope'))).toEqual([]);
    expect(loadWsFixtures(null)).toEqual([]);
  });

  it('replays the handshake fixture, advancing the clock to produce the heartbeat', async () => {
    const [handshake] = loadWsFixtures(dir);
    if (handshake === undefined) throw new Error('fixture missing');
    expect(await replay(handshake)).toBe(2);
  });

  it('replays a drop-and-resume fixture across two connections', async () => {
    const fixtures = loadWsFixtures(dir);
    const resume = fixtures.find((fixture) => fixture.name === 'resume');
    if (resume === undefined) throw new Error('fixture missing');
    expect(await replay(resume, { c1: 7n })).toBe(2);
  });

  it('fails loudly when the client sends something else', async () => {
    const broken: WsFixture = {
      name: 'broken',
      file: '<inline>',
      frames: [{ dir: 'c2s', json: { hello: { protoVersion: 99 } } }],
      clientReplay: true,
    };
    await expect(replay(broken)).rejects.toThrow(/expected 99, got 1/);
  });

  it('rejects a fixture entry with neither json nor close', () => {
    const bad = join(dir, 'bad.json');
    writeFileSync(bad, JSON.stringify({ frames: [{ dir: 'c2s' }] }), 'utf8');
    try {
      expect(() => loadWsFixtures(dir)).toThrow(/needs either "json" or "close"/);
    } finally {
      rmSync(bad, { force: true });
    }
  });
});
