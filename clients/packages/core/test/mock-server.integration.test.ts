import type { MockServer } from '@talky/test-utils';
import { ErrorCode } from '@talky/protocol/v1';
import { startMockServer } from '@talky/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { TalkyWsError, WsClient } from '../src/index.js';
import { createWsClient } from '../src/index.js';

/**
 * The one suite that uses real sockets: the `ws` mock server on a real port, driven by Node's
 * global `WebSocket` through `defaultWebSocketFactory` and real timers. Everything else runs
 * on the in-process transport, which is faster and fully deterministic.
 */

/** Poll `predicate` in real time. */
async function waitFor(predicate: () => boolean, timeoutMs = 5_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error('waitFor: timed out');
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

let server: MockServer | undefined;
let client: WsClient | undefined;

afterEach(async () => {
  client?.close();
  await server?.close();
  server = undefined;
  client = undefined;
});

function connect(target: MockServer, overrides: Parameters<typeof createWsClient>[0]): WsClient {
  return createWsClient({ ...overrides, url: target.url });
}

describe('mock server over real WebSockets', () => {
  it('completes the handshake and answers heartbeats', async () => {
    server = await startMockServer({ heartbeatIntervalMs: 120 });
    const errors: TalkyWsError[] = [];
    const rtts: number[] = [];
    client = connect(server, {
      url: server.url,
      getToken: () => Promise.resolve('dev:u1'),
      deviceId: 'device-1',
      client: { platform: 'desktop', appVersion: '0.1.0', os: 'Windows 11', locale: 'tr-TR' },
      pongTimeoutMs: 2_000,
    });
    client.on('error', (error) => errors.push(error));
    client.on('rtt', (rtt) => rtts.push(rtt));

    client.connect();
    await waitFor(() => client?.state === 'open');
    expect(client.welcome?.sessionId).toBe('session-1');

    await waitFor(() => rtts.length >= 2);
    expect(rtts.every((rtt) => rtt >= 0 && rtt < 2_000)).toBe(true);
    expect(errors).toEqual([]);

    const hello = server.received[0]?.frame.hello;
    expect(hello?.protoVersion).toBe(1);
    expect(hello?.client?.platform).toBe('desktop');
  });

  it('recovers from a dropped TCP connection', async () => {
    server = await startMockServer();
    let opens = 0;
    client = connect(server, {
      url: server.url,
      getToken: () => Promise.resolve('dev:u1'),
      deviceId: 'device-1',
      client: { platform: 'web', appVersion: '0.1.0', os: 'Windows 11', locale: 'tr-TR' },
      backoff: { initialMs: 10, maxMs: 50, factor: 2, jitter: 0 },
    });
    client.on('open', () => {
      opens += 1;
    });

    client.connect();
    await waitFor(() => opens === 1);
    server.dropConnection();
    await waitFor(() => opens === 2, 10_000);
    expect(server.connections).toHaveLength(2);
    expect(client.state).toBe('open');
  });

  it('rejects a bad token with 1008 and does not retry', async () => {
    server = await startMockServer({ authenticate: (hello) => hello.token === 'good' });
    const errors: TalkyWsError[] = [];
    client = connect(server, {
      url: server.url,
      getToken: () => Promise.resolve('bad'),
      deviceId: 'device-1',
      client: { platform: 'web', appVersion: '0.1.0', os: 'Windows 11', locale: 'tr-TR' },
    });
    client.on('error', (error) => errors.push(error));

    client.connect();
    await waitFor(() => client?.state === 'closed');
    expect(errors.some((error) => error.code === ErrorCode.UNAUTHENTICATED)).toBe(true);
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(server.connections).toHaveLength(1);
  });

  it('sends Bye and closes cleanly with 1000', async () => {
    server = await startMockServer();
    client = connect(server, {
      url: server.url,
      getToken: () => Promise.resolve('dev:u1'),
      deviceId: 'device-1',
      client: { platform: 'web', appVersion: '0.1.0', os: 'Windows 11', locale: 'tr-TR' },
    });
    client.connect();
    await waitFor(() => client?.state === 'open');

    const connection = server.latest();
    client.close('logout');
    await waitFor(() => connection.closed);
    expect(connection.received.at(-1)?.bye).toBeDefined();
    expect(connection.closeCode).toBe(1000);
  });
});
