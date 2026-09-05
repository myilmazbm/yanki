import { describe, expect, it } from 'vitest';
import { createHarness } from './helpers.js';

/** `random() === 0.5` cancels the jitter exactly, so the delay equals the raw backoff base. */
const NO_JITTER = (): number => 0.5;

describe('reconnect', () => {
  it('honours the Reconnect{delayMs} window with a uniform random delay', async () => {
    const harness = createHarness({ client: { random: () => 0.42 } });
    try {
      await harness.open();
      harness.server.sendReconnect(10_000, 'draining');
      await harness.waitUntil(() => harness.reconnects.length > 0);

      expect(harness.reconnects[0]).toMatchObject({
        reason: 'server-reconnect',
        delayMs: 4_200,
        attempt: 0,
      });

      // Not a millisecond early…
      await harness.advance(4_199);
      expect(harness.server.connections).toHaveLength(1);
      // …and not a millisecond late.
      await harness.advance(1);
      await harness.waitUntil(() => harness.client.state === 'open' && harness.opens() === 2);
      expect(harness.server.connections).toHaveLength(2);
    } finally {
      await harness.dispose();
    }
  });

  it('never delays a server-driven reconnect beyond delayMs', async () => {
    for (const sample of [0, 0.999999]) {
      const harness = createHarness({ client: { random: () => sample } });
      try {
        await harness.open();
        harness.server.sendReconnect(3_000);
        await harness.waitUntil(() => harness.reconnects.length > 0);
        const delay = harness.reconnects[0]?.delayMs ?? -1;
        expect(delay).toBeGreaterThanOrEqual(0);
        expect(delay).toBeLessThanOrEqual(3_000);
      } finally {
        await harness.dispose();
      }
    }
  });

  it('backs off 0.5 s → ×2 → 30 s while the handshake keeps failing', async () => {
    const harness = createHarness({
      client: { random: NO_JITTER },
      server: { autoWelcome: false },
    });
    try {
      harness.server.onRequest('hello', (_frame, connection) => {
        connection.drop();
        return undefined;
      });
      harness.client.connect();
      await harness.waitUntil(() => harness.reconnects.length >= 8, {
        maxMs: 600_000,
        stepMs: 500,
      });

      expect(harness.reconnects.slice(0, 8).map((info) => info.delayMs)).toEqual([
        500, 1_000, 2_000, 4_000, 8_000, 16_000, 30_000, 30_000,
      ]);
      expect(harness.reconnects.slice(0, 8).map((info) => info.attempt)).toEqual([
        1, 2, 3, 4, 5, 6, 7, 8,
      ]);
      expect(harness.reconnects[0]?.reason).toBe('transport');
      expect(harness.reconnects[0]?.closeCode).toBe(1006);
    } finally {
      await harness.dispose();
    }
  });

  it('resets the backoff after a successful Welcome', async () => {
    const harness = createHarness({ client: { random: NO_JITTER } });
    try {
      await harness.open();

      harness.server.dropConnection();
      await harness.waitUntil(() => harness.reconnects.length === 1);
      expect(harness.reconnects[0]?.delayMs).toBe(500);

      await harness.waitUntil(() => harness.client.state === 'open' && harness.opens() === 2);

      harness.server.dropConnection();
      await harness.waitUntil(() => harness.reconnects.length === 2);
      // Still 500 ms, not 1000: the completed handshake reset the counter.
      expect(harness.reconnects[1]?.delayMs).toBe(500);
      expect(harness.reconnects[1]?.attempt).toBe(1);
    } finally {
      await harness.dispose();
    }
  });

  it('resumes with the latest since cursor after a drop', async () => {
    let lastSeq = 7n;
    const harness = createHarness({ client: { getSince: () => ({ c1: lastSeq }) } });
    try {
      await harness.open();
      lastSeq = 11n;
      harness.server.dropConnection();
      await harness.waitUntil(() => harness.client.state === 'open' && harness.opens() === 2);

      const hellos = harness.server.received.filter((entry) => entry.frame.hello !== undefined);
      expect(hellos).toHaveLength(2);
      expect(hellos[0]?.frame.hello?.since).toEqual({ c1: '7' });
      expect(hellos[1]?.frame.hello?.since).toEqual({ c1: '11' });
    } finally {
      await harness.dispose();
    }
  });

  it('emits resyncRequired when the server cannot replay the gap', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.server.latest().sendResyncRequired(['c1', 'c2'], false);
      await harness.settle();

      expect(harness.resyncs).toHaveLength(1);
      expect(harness.resyncs[0]?.conversationIds).toEqual(['c1', 'c2']);
      expect(harness.resyncs[0]?.full).toBe(false);
      // A resync is not a connection problem: the session stays open.
      expect(harness.client.state).toBe('open');
    } finally {
      await harness.dispose();
    }
  });

  it('reports a request-less Error frame and follows the server close', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.server.latest().sendError('ERROR_CODE_INTERNAL', { message: 'node is unhappy' });
      await harness.settle();

      expect(harness.errors).toHaveLength(1);
      expect(harness.errors[0]?.reason).toBe('protocol');
      expect(harness.errors[0]?.message).toBe('node is unhappy');
      // INTERNAL is not fatal, so the client waits for the server rather than closing itself.
      expect(harness.client.state).toBe('open');

      harness.server.latest().close(1011, 'internal error');
      await harness.waitUntil(() => harness.reconnects.length === 1);
      expect(harness.reconnects[0]?.closeCode).toBe(1011);
    } finally {
      await harness.dispose();
    }
  });

  it('drops a replayed frame with an id it has already seen', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      const duplicate = { id: 'dup-1', resyncRequired: { conversationIds: ['c1'], full: false } };
      harness.server.send(duplicate);
      await harness.settle();
      harness.server.send(duplicate);
      await harness.settle();

      expect(harness.resyncs).toHaveLength(1);
      expect(harness.frames.filter((frame) => frame.id === 'dup-1')).toHaveLength(1);
    } finally {
      await harness.dispose();
    }
  });

  it('connect() after close() re-enables the client', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.client.close();
      await harness.settle();
      expect(harness.client.state).toBe('closed');

      await harness.open();
      expect(harness.client.state).toBe('open');
      expect(harness.opens()).toBe(2);
    } finally {
      await harness.dispose();
    }
  });

  it('sends Bye and closes with 1000 on close()', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      const connection = harness.server.latest();
      harness.client.close('signing out');
      await harness.settle();

      expect(connection.received.at(-1)?.bye).toBeDefined();
      expect(connection.closed).toBe(true);
      expect(connection.closeCode).toBe(1000);
      expect(harness.reconnects).toEqual([]);
      expect(harness.clock.pending).toBe(0);
    } finally {
      await harness.dispose();
    }
  });
});
