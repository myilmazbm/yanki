import { describe, expect, it } from 'vitest';
import { createHarness } from './helpers.js';

/**
 * `Reconnect{reason: "too_many_connections"}` means a newer connection of the same user is
 * live and this one was evicted. Reconnecting would evict the newer one in turn, so the
 * client must stop, report it, and leave the decision to the app ("signed in elsewhere").
 */
describe('server eviction', () => {
  it('closes without reconnecting and reports the eviction', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.server.latest().sendReconnect(0, 'too_many_connections');
      await harness.waitUntil(() => harness.client.state === 'closed', { maxMs: 10_000 });

      expect(harness.reconnects).toHaveLength(0);
      expect(harness.errors.some((error) => error.reason === 'evicted')).toBe(true);

      // Nothing happens later either: no timer is armed.
      await harness.advance(120_000);
      expect(harness.client.state).toBe('closed');
      expect(harness.opens()).toBe(1);
    } finally {
      await harness.dispose();
    }
  });

  it('emits the evicted event with the server reason', async () => {
    const harness = createHarness();
    const evictions: string[] = [];
    harness.client.on('evicted', (reason) => evictions.push(reason));
    try {
      await harness.open();
      harness.server.latest().sendReconnect(0, 'too_many_connections');
      await harness.waitUntil(() => harness.client.state === 'closed', { maxMs: 10_000 });
      expect(evictions).toEqual(['too_many_connections']);
    } finally {
      await harness.dispose();
    }
  });

  it('can be reconnected explicitly by the app afterwards', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.server.latest().sendReconnect(0, 'too_many_connections');
      await harness.waitUntil(() => harness.client.state === 'closed', { maxMs: 10_000 });

      harness.client.connect();
      await harness.waitUntil(() => harness.client.state === 'open', { maxMs: 60_000 });
      expect(harness.opens()).toBe(2);
    } finally {
      await harness.dispose();
    }
  });

  it('still reconnects for a draining node', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.server.latest().sendReconnect(2_000, 'draining');
      await harness.waitUntil(() => harness.client.state === 'reconnecting', { maxMs: 10_000 });
      expect(harness.reconnects).toHaveLength(1);
      expect(harness.reconnects[0]?.reason).toBe('server-reconnect');
    } finally {
      await harness.dispose();
    }
  });
});
