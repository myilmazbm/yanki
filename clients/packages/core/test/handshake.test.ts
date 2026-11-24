import { describe, expect, it } from 'vitest';
import { ErrorCode } from '@talky/protocol/v1';
import { isUuidV7 } from '../src/index.js';
import { createHarness } from './helpers.js';

describe('handshake', () => {
  it('sends Hello first and opens on Welcome', async () => {
    const harness = createHarness();
    try {
      await harness.open();

      expect(harness.client.state).toBe('open');
      expect(harness.states).toEqual(['connecting', 'authenticating', 'open']);
      expect(harness.opens()).toBe(1);
      expect(harness.welcomes).toHaveLength(1);
      expect(harness.welcomes[0]?.heartbeatIntervalMs).toBe(25_000);
      expect(harness.client.welcome?.sessionId).toBe('session-1');
      expect(harness.errors).toEqual([]);

      const [first] = harness.server.received;
      expect(first?.frame.hello).toBeDefined();
      expect(first?.frame.hello?.protoVersion).toBe(1);
      expect(first?.frame.hello?.token).toBe('dev:u1');
      expect(first?.frame.hello?.deviceId).toBe('11111111-2222-4333-8444-555555555555');
      expect(first?.frame.hello?.client).toEqual({
        platform: 'web',
        appVersion: '0.1.0',
        os: 'Windows 11',
        locale: 'tr-TR',
      });
      expect(isUuidV7(first?.frame.rid ?? '')).toBe(true);
    } finally {
      await harness.dispose();
    }
  });

  it('carries the resume cursor from getSince, as int64 strings', async () => {
    const harness = createHarness({
      client: { getSince: () => ({ c1: 42n, c2: '9007199254740993' }) },
    });
    try {
      await harness.open();
      expect(harness.server.received[0]?.frame.hello?.since).toEqual({
        c1: '42',
        c2: '9007199254740993',
      });
    } finally {
      await harness.dispose();
    }
  });

  it('truncates the resume cursor to the 50-entry protocol maximum', async () => {
    const since: Record<string, bigint> = {};
    for (let i = 0; i < 120; i += 1) since[`c${i.toString()}`] = BigInt(i);
    const harness = createHarness({ client: { getSince: () => since } });
    try {
      await harness.open();
      const sent = harness.server.received[0]?.frame.hello?.since ?? {};
      expect(Object.keys(sent)).toHaveLength(50);
      expect(sent.c0).toBe('0');
      expect(sent.c49).toBe('49');
      expect(sent.c50).toBeUndefined();
    } finally {
      await harness.dispose();
    }
  });

  it('re-reads the token on every attempt so a refreshed token is used', async () => {
    const tokens = ['stale', 'fresh'];
    let index = 0;
    const harness = createHarness({
      client: {
        getToken: () => {
          const token = tokens[Math.min(index, tokens.length - 1)] ?? '';
          index += 1;
          return Promise.resolve(token);
        },
      },
    });
    try {
      await harness.open();
      harness.server.dropConnection();
      await harness.waitUntil(() => harness.client.state === 'open' && harness.opens() === 2);
      const hellos = harness.server.received.filter((entry) => entry.frame.hello !== undefined);
      expect(hellos.map((entry) => entry.frame.hello?.token)).toEqual(['stale', 'fresh']);
    } finally {
      await harness.dispose();
    }
  });

  it('reports an unsupported protocol version and stops retrying (close 1002)', async () => {
    const harness = createHarness({ server: { protoVersion: 2 } });
    try {
      harness.client.connect();
      await harness.waitUntil(() => harness.client.state === 'closed');

      expect(harness.errors.map((error) => error.code)).toContain(
        ErrorCode.PROTOCOL_VERSION_UNSUPPORTED,
      );
      expect(harness.reconnects).toEqual([]);

      // No amount of time may produce another attempt: that is the "no infinite loop" rule.
      await harness.advance(300_000);
      expect(harness.client.state).toBe('closed');
      expect(harness.server.connections).toHaveLength(1);
      expect(harness.clock.pending).toBe(0);
    } finally {
      await harness.dispose();
    }
  });

  it('stops reconnecting after an authentication failure (close 1008)', async () => {
    const harness = createHarness({ server: { authenticate: () => false } });
    try {
      harness.client.connect();
      await harness.waitUntil(() => harness.client.state === 'closed');

      const authError = harness.errors.find((error) => error.code === ErrorCode.UNAUTHENTICATED);
      expect(authError).toBeDefined();
      expect(authError?.reason).toBe('protocol');
      expect(harness.reconnects).toEqual([]);

      await harness.advance(300_000);
      expect(harness.server.connections).toHaveLength(1);
      expect(harness.clock.pending).toBe(0);
    } finally {
      await harness.dispose();
    }
  });

  it('keeps retrying an authentication failure when retryOnAuthFailure is set', async () => {
    const harness = createHarness({
      server: { authenticate: () => false },
      client: { retryOnAuthFailure: true },
    });
    try {
      harness.client.connect();
      await harness.waitUntil(() => harness.server.connections.length >= 3, { maxMs: 60_000 });
      expect(harness.reconnects.length).toBeGreaterThanOrEqual(2);
      expect(harness.client.state).not.toBe('closed');
    } finally {
      await harness.dispose();
    }
  });

  it('emits an error and reconnects when Welcome never arrives', async () => {
    const harness = createHarness({ server: { autoWelcome: false } });
    try {
      harness.client.connect();
      await harness.waitUntil(() => harness.client.state === 'authenticating');
      await harness.advance(15_000);
      await harness.waitUntil(() => harness.reconnects.length > 0);
      expect(harness.errors.some((error) => error.reason === 'timeout')).toBe(true);
    } finally {
      await harness.dispose();
    }
  });

  it('surfaces a rejected getToken as a transport error and retries', async () => {
    let attempt = 0;
    const harness = createHarness({
      client: {
        getToken: () => {
          attempt += 1;
          return attempt === 1 ? Promise.reject(new Error('no session')) : Promise.resolve('ok');
        },
      },
    });
    try {
      harness.client.connect();
      await harness.waitUntil(() => harness.client.state === 'open', { maxMs: 60_000 });
      expect(harness.errors[0]?.reason).toBe('transport');
      expect(harness.reconnects[0]?.reason).toBe('token');
    } finally {
      await harness.dispose();
    }
  });

  it('stays closed when an error listener calls close() during a failed open', async () => {
    const harness = createHarness({
      client: { getToken: () => Promise.reject(new Error('signed out')) },
    });
    try {
      harness.client.on('error', () => {
        harness.client.close();
      });
      harness.client.connect();
      await harness.waitUntil(() => harness.errors.length > 0);

      expect(harness.client.state).toBe('closed');
      expect(harness.reconnects).toEqual([]);
      expect(harness.clock.pending).toBe(0);
    } finally {
      await harness.dispose();
    }
  });
});
