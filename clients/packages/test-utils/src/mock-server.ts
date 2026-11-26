import { once } from 'node:events';
import type { AddressInfo } from 'node:net';
import type { RawData, WebSocket } from 'ws';
import { WebSocketServer } from 'ws';
import type { ConnectionSink } from './server-core.js';
import { MockServerCore } from './server-core.js';
import type { MockServerOptions } from './types.js';

/**
 * Protocol v1 is text-only, so a binary frame is a protocol violation. Feeding this marker
 * into the session makes the server answer with INVALID_FRAME and close 1003, exactly as the
 * real server does.
 */
const BINARY_FRAME_MARKER = '<binary frame>';

/** `ws` hands text frames over as a `Buffer`, an `ArrayBuffer` or a `Buffer[]` fragment list. */
function rawToText(data: RawData): string {
  if (Buffer.isBuffer(data)) return data.toString('utf8');
  if (Array.isArray(data)) return Buffer.concat(data).toString('utf8');
  return Buffer.from(data).toString('utf8');
}

/** Options for {@link startMockServer}. */
export interface MockServerStartOptions extends MockServerOptions {
  /** TCP port. Default `0` — the OS picks a free one. */
  port?: number;
  /** Path the endpoint is advertised on. Default `/ws`. */
  path?: string;
}

/** A mock Yanki server listening on a real TCP port. */
export interface MockServer extends MockServerCore {
  /** Full `ws://…` URL to hand to `createWsClient({ url })`. */
  readonly url: string;
  readonly port: number;
}

/**
 * Start a mock Yanki realtime server over real WebSockets (the `ws` package).
 *
 * Use it for the handful of tests that must exercise a genuine socket — the browser/Node
 * global `WebSocket`, real close codes, real backpressure. Everything else is faster and more
 * deterministic against `startMemoryServer()`.
 */
export async function startMockServer(options: MockServerStartOptions = {}): Promise<MockServer> {
  const wss = new WebSocketServer({ port: options.port ?? 0 });
  await once(wss, 'listening');
  const address = wss.address() as AddressInfo | string;
  const port = typeof address === 'string' ? 0 : address.port;

  const core = new MockServerCore(options);
  const sockets = new Set<WebSocket>();

  wss.on('connection', (socket: WebSocket) => {
    sockets.add(socket);
    const sink: ConnectionSink = {
      sendText: (text) => {
        if (socket.readyState === socket.OPEN) socket.send(text);
      },
      closeSocket: (code, reason) => {
        socket.close(code, reason);
      },
      dropSocket: () => {
        socket.terminate();
      },
    };
    const session = core.register(sink);
    socket.on('message', (data: RawData, isBinary: boolean) => {
      session.handleText(isBinary ? BINARY_FRAME_MARKER : rawToText(data));
    });
    socket.on('close', (code: number) => {
      sockets.delete(socket);
      session.markClosed(code);
    });
    socket.on('error', () => {
      /* the close handler does the bookkeeping */
    });
  });

  core.closeHook = async (): Promise<void> => {
    for (const socket of sockets) socket.terminate();
    sockets.clear();
    await new Promise<void>((resolve) => {
      wss.close(() => {
        resolve();
      });
    });
  };

  const path = options.path ?? '/ws';
  return Object.assign(core, { url: `ws://127.0.0.1:${port.toString()}${path}`, port });
}
