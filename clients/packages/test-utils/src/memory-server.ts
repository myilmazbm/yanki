import type { ConnectionSink, MockSession } from './server-core.js';
import { MockServerCore } from './server-core.js';
import type {
  CloseEventLike,
  MessageEventLike,
  MockServerOptions,
  WebSocketFactory,
  WebSocketLike,
} from './types.js';

const CONNECTING = 0;
const OPEN = 1;
const CLOSING = 2;
const CLOSED = 3;

/**
 * In-process `WebSocketLike`.
 *
 * Every hand-off (open, message, close) is deferred by one microtask, which is exactly what a
 * real socket does relative to the caller and what keeps the client's own re-entrancy
 * assumptions honest — while remaining fully deterministic and free of real I/O.
 */
export class MemorySocket implements WebSocketLike {
  readyState = CONNECTING;
  onopen: ((event: unknown) => void) | null = null;
  onmessage: ((event: MessageEventLike) => void) | null = null;
  onclose: ((event: CloseEventLike) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;

  readonly session: MockSession;
  /** Set as soon as a close has been queued, so no frame can overtake it. */
  private closePending = false;

  constructor(
    readonly url: string,
    core: MockServerCore,
  ) {
    const sink: ConnectionSink = {
      sendText: (text) => {
        // Queued, never dropped: a frame sent immediately before a close (an `Error` before a
        // 1008, say) must still reach the client, exactly as it would over TCP.
        queueMicrotask(() => {
          if (this.readyState === CLOSED) return;
          this.onmessage?.({ data: text });
        });
      },
      closeSocket: (code, reason) => {
        this.finishClose(code, reason, true);
      },
      dropSocket: () => {
        this.finishClose(1006, '', false);
      },
    };
    this.session = core.register(sink);
    queueMicrotask(() => {
      if (this.readyState !== CONNECTING) return;
      this.readyState = OPEN;
      this.onopen?.(undefined);
    });
  }

  send(data: string): void {
    if (this.readyState !== OPEN) {
      throw new Error(
        `MemorySocket.send: socket is not open (readyState ${this.readyState.toString()})`,
      );
    }
    queueMicrotask(() => {
      if (this.session.closed) return;
      this.session.handleText(data);
    });
  }

  close(code = 1000, reason = ''): void {
    if (this.readyState === CLOSED || this.readyState === CLOSING) return;
    this.readyState = CLOSING;
    queueMicrotask(() => {
      this.session.markClosed(code);
      this.finishClose(code, reason, true);
    });
  }

  /** True while the client still holds handlers on this socket — used by leak assertions. */
  get hasHandlers(): boolean {
    return (
      this.onopen !== null ||
      this.onmessage !== null ||
      this.onclose !== null ||
      this.onerror !== null
    );
  }

  private finishClose(code: number, reason: string, wasClean: boolean): void {
    if (this.closePending || this.readyState === CLOSED) return;
    this.closePending = true;
    this.readyState = CLOSING;
    queueMicrotask(() => {
      this.readyState = CLOSED;
      this.onclose?.({ code, reason, wasClean });
    });
  }
}

/** A {@link MockServerCore} wired to in-process sockets. */
export interface MemoryServer extends MockServerCore {
  /** Hand this to `createWsClient({ webSocketFactory })`. */
  readonly webSocketFactory: WebSocketFactory;
  /** Every client-side socket ever created. */
  readonly sockets: readonly MemorySocket[];
  /** Sockets the client has not detached its handlers from. Should be 0 after `close()`. */
  socketsWithHandlers(): number;
}

/**
 * Start a mock Yanki server that speaks over in-process sockets.
 *
 * Preferred for protocol/state-machine tests: no ports, no I/O, no flakiness, and it composes
 * with `createFakeClock()` so a whole reconnect storm runs in microseconds.
 */
export function startMemoryServer(options: MockServerOptions = {}): MemoryServer {
  const core = new MockServerCore(options);
  const sockets: MemorySocket[] = [];
  const factory: WebSocketFactory = (url) => {
    const socket = new MemorySocket(url, core);
    sockets.push(socket);
    return socket;
  };
  return Object.assign(core, {
    webSocketFactory: factory,
    sockets,
    socketsWithHandlers: (): number => sockets.filter((socket) => socket.hasHandlers).length,
  });
}
