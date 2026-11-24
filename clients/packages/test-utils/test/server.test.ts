import { describe, expect, it } from 'vitest';
import type { ClientFrameJson } from '@talky/protocol/v1';
import { ackFrame, flush, startMemoryServer, startMockServer } from '../src/index.js';

/** Drive the server the way a client would, without depending on `@talky/core`. */
function open(url = 'ws://mock/ws'): ReturnType<typeof startMemoryServer> {
  return startMemoryServer({ welcome: { sessionId: url } });
}

async function connectRaw(
  server: ReturnType<typeof startMemoryServer>,
): Promise<{ socket: ReturnType<typeof server.webSocketFactory>; inbox: string[] }> {
  const socket = server.webSocketFactory('ws://mock/ws');
  const inbox: string[] = [];
  socket.onmessage = (event) => {
    if (typeof event.data === 'string') inbox.push(event.data);
  };
  await flush();
  return { socket, inbox };
}

const HELLO = JSON.stringify({ rid: 'r1', hello: { protoVersion: 1, token: 'dev:u1' } });

describe('startMemoryServer', () => {
  it('welcomes a valid Hello and pongs a Ping', async () => {
    const server = open();
    const { socket, inbox } = await connectRaw(server);

    socket.send(HELLO);
    await flush();
    const welcome = JSON.parse(inbox[0] ?? '{}') as Record<string, unknown>;
    expect(welcome.welcome).toMatchObject({ heartbeatIntervalMs: 25_000 });

    socket.send(JSON.stringify({ ping: { ts: '42' } }));
    await flush();
    const pong = JSON.parse(inbox[1] ?? '{}') as { pong?: { ts?: string } };
    expect(pong.pong?.ts).toBe('42');

    await server.close();
  });

  it('rejects a frame that arrives before Hello', async () => {
    const server = open();
    const { socket, inbox } = await connectRaw(server);
    socket.send(JSON.stringify({ ping: { ts: '1' } }));
    await flush();
    const error = JSON.parse(inbox[0] ?? '{}') as { error?: { code?: string } };
    expect(error.error?.code).toBe('ERROR_CODE_HELLO_REQUIRED');
    expect(server.latest().closeCode).toBe(1008);
    await server.close();
  });

  it('rejects an unsupported protocol version with 1002', async () => {
    const server = startMemoryServer({ protoVersion: 1 });
    const { socket, inbox } = await connectRaw(server);
    socket.send(JSON.stringify({ rid: 'r1', hello: { protoVersion: 7 } }));
    await flush();
    const error = JSON.parse(inbox[0] ?? '{}') as { error?: { code?: string; rid?: string } };
    expect(error.error?.code).toBe('ERROR_CODE_PROTOCOL_VERSION_UNSUPPORTED');
    expect(error.error?.rid).toBe('r1');
    expect(server.latest().closeCode).toBe(1002);
    await server.close();
  });

  it('answers a second Hello with ALREADY_AUTHENTICATED and stays open', async () => {
    const server = open();
    const { socket, inbox } = await connectRaw(server);
    socket.send(HELLO);
    await flush();
    socket.send(HELLO);
    await flush();
    const error = JSON.parse(inbox[1] ?? '{}') as { error?: { code?: string } };
    expect(error.error?.code).toBe('ERROR_CODE_ALREADY_AUTHENTICATED');
    expect(server.latest().closed).toBe(false);
    await server.close();
  });

  it('stops answering pings after simulateSilence', async () => {
    const server = open();
    const { socket, inbox } = await connectRaw(server);
    socket.send(HELLO);
    await flush();
    server.simulateSilence();
    socket.send(JSON.stringify({ ping: { ts: '1' } }));
    await flush();
    expect(inbox).toHaveLength(1);
    await server.close();
  });

  it('scripts replies by rid and resolves expectFrame', async () => {
    const server = open();
    const { socket, inbox } = await connectRaw(server);
    server.onRequest('ping', (frame) => ackFrame(frame.rid ?? '', { id: 'm1' }));
    socket.send(HELLO);
    await flush();

    const expected = server.expectFrame('ping');
    socket.send(JSON.stringify({ rid: 'r2', ping: { ts: '1' } }));
    const frame: ClientFrameJson = await expected;
    await flush();
    expect(frame.rid).toBe('r2');
    const ack = JSON.parse(inbox.at(-1) ?? '{}') as { ack?: { rid?: string; id?: string } };
    expect(ack.ack).toMatchObject({ rid: 'r2', id: 'm1' });
    await server.close();
  });

  it('rejects invalid proto3 JSON with INVALID_FRAME and 1003', async () => {
    const server = open();
    const { socket, inbox } = await connectRaw(server);
    socket.send('{"hello":{"protoVersion":"not a number"}}');
    await flush();
    const error = JSON.parse(inbox[0] ?? '{}') as { error?: { code?: string } };
    expect(error.error?.code).toBe('ERROR_CODE_INVALID_FRAME');
    expect(server.latest().closeCode).toBe(1003);
    await server.close();
  });

  it('drops a connection abruptly with close code 1006', async () => {
    const server = open();
    const { socket } = await connectRaw(server);
    let observed: number | null = null;
    socket.onclose = (event) => {
      observed = event.code;
    };
    socket.send(HELLO);
    await flush();
    server.dropConnection();
    await flush();
    expect(observed).toBe(1006);
    await server.close();
  });

  it('times out expectFrame with a useful message', async () => {
    const server = open();
    await connectRaw(server);
    await expect(server.expectFrame('bye', { timeoutMs: 20 })).rejects.toThrow(/body case 'bye'/);
    await server.close();
  });
});

describe('startMockServer', () => {
  it('listens on a real port and speaks the same protocol', async () => {
    const server = await startMockServer();
    expect(server.url).toMatch(/^ws:\/\/127\.0\.0\.1:\d+\/ws$/);

    const socket = new WebSocket(server.url);
    const inbox: string[] = [];
    socket.onmessage = (event: MessageEvent) => {
      if (typeof event.data === 'string') inbox.push(event.data);
    };
    await new Promise<void>((resolve) => {
      socket.onopen = () => {
        resolve();
      };
    });

    socket.send(HELLO);
    await server.expectFrame('hello');
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 50);
    });
    expect(inbox).toHaveLength(1);
    expect(JSON.parse(inbox[0] ?? '{}')).toHaveProperty('welcome');

    socket.close();
    await server.close();
  });
});
