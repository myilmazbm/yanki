import { create } from '@bufbuild/protobuf';
import { ErrorCode, PingSchema } from '@yanki/protocol/v1';
import { ackFrame, errorFrame } from '@yanki/test-utils';
import { describe, expect, it } from 'vitest';
import type { ClientFrameBody } from '../src/index.js';
import { isYankiWsError, isUuidV7, YankiWsError } from '../src/index.js';
import { createHarness } from './helpers.js';

/**
 * Protocol v1 has no request-bearing body yet (`SendMessage` lands in M1), so the correlation
 * machinery is exercised with a `Ping` body plus a scripted reply — which is exactly what the
 * real frames will do.
 */
const REQUEST_BODY: ClientFrameBody = { case: 'ping', value: create(PingSchema, { ts: 1n }) };

/** Await a request that must reject, and hand back the typed error. */
async function failure(promise: Promise<unknown>): Promise<YankiWsError> {
  try {
    await promise;
  } catch (error) {
    if (isYankiWsError(error)) return error;
    throw error;
  }
  throw new Error('expected the request to reject');
}

describe('request correlation', () => {
  it('assigns a UUIDv7 rid and resolves on the matching Ack', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.server.onRequest('ping', (frame) =>
        ackFrame(frame.rid ?? '', { id: 'm1', seq: '7' }),
      );

      const ack = await harness.client.request(REQUEST_BODY);
      expect(ack.id).toBe('m1');
      expect(ack.seq).toBe(7n);
      expect(isUuidV7(ack.rid)).toBe(true);

      const sent = harness.server.received.find((entry) => entry.frame.ping !== undefined);
      expect(sent?.frame.rid).toBe(ack.rid);
    } finally {
      await harness.dispose();
    }
  });

  it('rejects with a typed error on a matching Error frame', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.server.onRequest('ping', (frame) =>
        errorFrame('ERROR_CODE_RATE_LIMITED', {
          rid: frame.rid ?? '',
          message: 'slow down',
          retryAfterMs: 250,
        }),
      );

      const error = await failure(harness.client.request(REQUEST_BODY));
      expect(error.reason).toBe('protocol');
      expect(error.code).toBe(ErrorCode.RATE_LIMITED);
      expect(error.retryAfterMs).toBe(250);
      expect(error.message).toBe('slow down');
      // A rejected request is not also a global error.
      expect(harness.errors).toEqual([]);
    } finally {
      await harness.dispose();
    }
  });

  it('times out after requestTimeoutMs', async () => {
    const harness = createHarness({ client: { requestTimeoutMs: 15_000 } });
    try {
      await harness.open();
      const settled = failure(harness.client.request(REQUEST_BODY));

      await harness.advance(14_999);
      harness.clock.advance(1);
      const error = await settled;
      expect(error.reason).toBe('timeout');
      expect(error.message).toContain('15000');
    } finally {
      await harness.dispose();
    }
  });

  it('honours a per-call timeout override', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      const settled = failure(harness.client.request(REQUEST_BODY, { timeoutMs: 400 }));
      await harness.advance(401);
      expect((await settled).reason).toBe('timeout');
    } finally {
      await harness.dispose();
    }
  });

  it('rejects every in-flight request when the connection drops', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      const first = failure(harness.client.request(REQUEST_BODY));
      const second = failure(harness.client.request(REQUEST_BODY));

      harness.server.dropConnection();
      await harness.settle();

      const [a, b] = await Promise.all([first, second]);
      expect(a.reason).toBe('disconnected');
      expect(b.reason).toBe('disconnected');
      expect(a.closeCode).toBe(1006);
    } finally {
      await harness.dispose();
    }
  });

  it('rejects in-flight requests with reason "closed" when the app closes the client', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      const settled = failure(harness.client.request(REQUEST_BODY));
      harness.client.close();
      await harness.settle();
      expect((await settled).reason).toBe('closed');
    } finally {
      await harness.dispose();
    }
  });

  it('rejects a request made while the client is not open', async () => {
    const harness = createHarness();
    try {
      const error = await failure(harness.client.request(REQUEST_BODY));
      expect(error.reason).toBe('disconnected');
      expect(error.message).toContain("'idle'");
    } finally {
      await harness.dispose();
    }
  });

  it('throws from send() when the client is not open', async () => {
    const harness = createHarness();
    try {
      expect(() => {
        harness.client.send(REQUEST_BODY);
      }).toThrow(YankiWsError);
    } finally {
      await harness.dispose();
    }
  });

  it('leaves no timer behind once a request settles', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.server.onRequest('ping', (frame) => ackFrame(frame.rid ?? ''));
      const pendingBefore = harness.clock.pending;
      await harness.client.request(REQUEST_BODY);
      await harness.settle();
      expect(harness.clock.pending).toBe(pendingBefore);
    } finally {
      await harness.dispose();
    }
  });

  it('ignores an Ack for an unknown rid without disturbing the session', async () => {
    const harness = createHarness();
    try {
      await harness.open();
      harness.server.send(ackFrame('0190f0a1-0000-7000-8000-00000000dead'));
      await harness.settle();
      expect(harness.client.state).toBe('open');
      expect(harness.errors).toEqual([]);
    } finally {
      await harness.dispose();
    }
  });
});
