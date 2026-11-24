import { pongFrame } from '@talky/test-utils';
import { describe, expect, it } from 'vitest';
import { createHarness } from './helpers.js';

/** Count the `Ping` frames the server has seen. */
function pings(received: readonly { frame: { ping?: unknown } }[]): number {
  return received.filter((entry) => entry.frame.ping !== undefined).length;
}

describe('heartbeat', () => {
  it('pings at Welcome.heartbeatIntervalMs and measures RTT from the echoed ts', async () => {
    const harness = createHarness({ server: { heartbeatIntervalMs: 5_000, autoPong: false } });
    try {
      await harness.open();
      expect(pings(harness.server.received)).toBe(0);

      await harness.advance(5_000);
      expect(pings(harness.server.received)).toBe(1);
      const ping = harness.server.received.find((entry) => entry.frame.ping !== undefined);
      const ts = ping?.frame.ping?.ts ?? '0';
      expect(ts).toBe(String(harness.clock.time));

      // 120 ms of virtual time pass before the Pong comes back.
      await harness.advance(120);
      harness.server.send(pongFrame(ts));
      await harness.settle();

      expect(harness.rtts).toEqual([120]);
      expect(harness.client.rttMs).toBe(120);
    } finally {
      await harness.dispose();
    }
  });

  it('treats a missing Pong as a dead link and reconnects', async () => {
    const harness = createHarness({ client: { pongTimeoutMs: 10_000 } });
    try {
      await harness.open();
      harness.server.simulateSilence();

      await harness.advance(25_000); // heartbeat goes out
      expect(harness.client.state).toBe('open');

      await harness.advance(10_000); // pong deadline expires
      expect(harness.errors.some((error) => error.reason === 'heartbeat')).toBe(true);
      expect(harness.reconnects).toHaveLength(1);

      await harness.waitUntil(() => harness.client.state === 'open' && harness.opens() === 2);
      expect(harness.server.connections).toHaveLength(2);
    } finally {
      await harness.dispose();
    }
  });

  it('does not let a heartbeat shorter than the pong timeout postpone the deadline', async () => {
    const harness = createHarness({
      client: { pongTimeoutMs: 10_000 },
      server: { heartbeatIntervalMs: 5_000 },
    });
    try {
      await harness.open();
      harness.server.simulateSilence();

      // Pings at 5 s and 10 s; the deadline armed by the first one expires at 15 s.
      await harness.advance(15_000);
      expect(harness.errors.filter((error) => error.reason === 'heartbeat')).toHaveLength(1);
      expect(harness.reconnects).toHaveLength(1);
    } finally {
      await harness.dispose();
    }
  });

  it('stops the heartbeat once the client is closed', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.client.close();
      await harness.settle();
      expect(harness.client.state).toBe('closed');
      expect(harness.clock.pending).toBe(0);

      await harness.advance(120_000);
      expect(pings(harness.server.received)).toBe(0);
    } finally {
      await harness.dispose();
    }
  });
});
