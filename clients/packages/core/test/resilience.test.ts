import { ErrorCode } from '@yanki/protocol/v1';
import { describe, expect, it } from 'vitest';
import { createHarness, seededRandom } from './helpers.js';

type Disruption = 'drop' | 'server-close' | 'server-reconnect' | 'silence' | 'error-then-close';

const DISRUPTIONS: readonly Disruption[] = [
  'drop',
  'server-close',
  'server-reconnect',
  'silence',
  'error-then-close',
];

describe('resilience', () => {
  it(
    'recovers from 200 randomised disruption cycles and leaks nothing',
    { timeout: 60_000 },
    async () => {
      const seed = 0xc0ffee;
      const pick = seededRandom(seed);
      const harness = createHarness({ seed, client: { pongTimeoutMs: 10_000 } });
      const used = new Map<Disruption, number>();

      try {
        await harness.open();

        for (let cycle = 0; cycle < 200; cycle += 1) {
          const disruption = DISRUPTIONS[Math.floor(pick() * DISRUPTIONS.length)] ?? 'drop';
          used.set(disruption, (used.get(disruption) ?? 0) + 1);
          const connection = harness.server.latest();

          switch (disruption) {
            case 'drop':
              connection.drop();
              break;
            case 'server-close':
              connection.close(1001, 'going away');
              break;
            case 'server-reconnect':
              connection.sendReconnect(Math.floor(pick() * 5_000));
              break;
            case 'silence':
              connection.simulateSilence();
              // One heartbeat window, then the pong deadline.
              await harness.advance(25_000);
              await harness.advance(10_001);
              break;
            case 'error-then-close':
              connection.sendError('ERROR_CODE_UNAVAILABLE', { retryAfterMs: 100 });
              await harness.settle();
              connection.close(1012, 'service restart');
              break;
          }

          const target = cycle + 2;
          await harness.waitUntil(
            () => harness.client.state === 'open' && harness.opens() === target,
            { maxMs: 120_000, stepMs: 250 },
          );

          // Steady state between cycles: exactly one armed timer, the heartbeat.
          expect(harness.clock.pending).toBe(1);
        }

        expect(harness.client.state).toBe('open');
        expect(harness.opens()).toBe(201);
        expect(harness.server.connections).toHaveLength(201);
        // Every kind of disruption was actually exercised.
        expect(used.size).toBe(DISRUPTIONS.length);

        harness.client.close();
        await harness.settle();

        expect(harness.client.state).toBe('closed');
        expect(harness.clock.pending).toBe(0);
        expect(harness.server.socketsWithHandlers()).toBe(0);
      } finally {
        await harness.dispose();
      }
    },
  );

  it('never lets a throwing listener break the socket loop', async () => {
    const harness = createHarness();
    try {
      harness.client.on('welcome', () => {
        throw new Error('listener exploded');
      });
      harness.client.on('frame', () => {
        throw new Error('frame listener exploded');
      });

      await harness.open();
      expect(harness.client.state).toBe('open');

      const handlerErrors = harness.errors.filter((error) => error.reason === 'handler');
      expect(handlerErrors.length).toBeGreaterThanOrEqual(2);
      expect(handlerErrors[0]?.cause).toBeInstanceOf(Error);

      // The session keeps working afterwards.
      harness.server.dropConnection();
      await harness.waitUntil(() => harness.client.state === 'open' && harness.opens() === 2);
    } finally {
      await harness.dispose();
    }
  });

  it('survives a throwing error listener without recursing', async () => {
    const harness = createHarness();
    try {
      let seen = 0;
      harness.client.on('error', () => {
        seen += 1;
        throw new Error('error listener exploded');
      });
      await harness.open();
      harness.server.latest().sendError('ERROR_CODE_INTERNAL', { message: 'boom' });
      await harness.settle();
      expect(seen).toBe(1);
      expect(harness.client.state).toBe('open');
    } finally {
      await harness.dispose();
    }
  });

  it('reports an undecodable frame without dropping the connection', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      const socket = harness.server.sockets.at(-1);
      socket?.onmessage?.({ data: '{ this is not json' });
      await harness.settle();

      expect(harness.errors.map((error) => error.reason)).toContain('decode');
      expect(harness.client.state).toBe('open');
    } finally {
      await harness.dispose();
    }
  });

  it('reports a non-string payload as a decode error', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      const socket = harness.server.sockets.at(-1);
      socket?.onmessage?.({ data: new Uint8Array([1, 2, 3]) });
      await harness.settle();

      expect(harness.errors.some((error) => error.reason === 'decode')).toBe(true);
      expect(harness.client.state).toBe('open');
    } finally {
      await harness.dispose();
    }
  });

  it('flags a server frame with no known body', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.server.send({ id: 'future-1' });
      await harness.settle();

      const error = harness.errors.find((entry) => entry.code === ErrorCode.INVALID_FRAME);
      expect(error).toBeDefined();
      expect(harness.client.state).toBe('open');
    } finally {
      await harness.dispose();
    }
  });
});
