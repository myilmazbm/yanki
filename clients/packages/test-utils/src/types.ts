import type {
  ClientFrameJson,
  ErrorCodeJson,
  ServerFrameJson,
  WelcomeJson,
} from '@talky/protocol/v1';

/**
 * `@talky/test-utils` deliberately does **not** depend on `@talky/core`.
 *
 * A dependency the other way round already exists (core's tests use these helpers), and
 * Turborepo refuses a cycle in the task graph. The two interfaces below are therefore
 * structural mirrors of `core`'s `WebSocketLike` and `TimerHost`; `core`'s test suite pins the
 * two together with a compile-time assertion, so a drift breaks the build immediately.
 */

/** Mirror of `@talky/core`'s `WebSocketMessageEventLike`. */
export interface MessageEventLike {
  readonly data: unknown;
}

/** Mirror of `@talky/core`'s `WebSocketCloseEventLike`. */
export interface CloseEventLike {
  readonly code: number;
  readonly reason: string;
  readonly wasClean?: boolean;
}

/** Mirror of `@talky/core`'s `WebSocketLike`. */
export interface WebSocketLike {
  readonly readyState: number;
  send(data: string): void;
  close(code?: number, reason?: string): void;
  onopen: ((event: unknown) => void) | null;
  onmessage: ((event: MessageEventLike) => void) | null;
  onclose: ((event: CloseEventLike) => void) | null;
  onerror: ((event: unknown) => void) | null;
}

/** Mirror of `@talky/core`'s `WebSocketFactory`. */
export type WebSocketFactory = (url: string, protocols?: readonly string[]) => WebSocketLike;

/** Mirror of `@talky/core`'s `TimerHost`. */
export interface TimerHostLike {
  setTimeout(handler: () => void, timeoutMs: number): unknown;
  clearTimeout(handle: unknown): void;
  now(): number;
}

/** Outcome of the mock server's token check. */
export interface MockAuthResult {
  ok: boolean;
  /** Error code sent when `ok` is false. Defaults to `ERROR_CODE_UNAUTHENTICATED`. */
  code?: ErrorCodeJson;
  /** Close code sent after the error. Defaults to 1008. */
  closeCode?: number;
  message?: string;
}

/** How the mock server behaves. Every knob has a protocol-conformant default. */
export interface MockServerOptions {
  /** Reply to a valid `Hello` with a `Welcome`. Default `true`. */
  autoWelcome?: boolean;
  /** Reply to `Ping` with `Pong`. Default `true`. */
  autoPong?: boolean;
  /** Reply to every request-bearing frame with an `Ack`. Default `false` (script it instead). */
  autoAck?: boolean;
  /** Advertised in `Welcome.heartbeatIntervalMs`. Default 25 000. */
  heartbeatIntervalMs?: number;
  /** The only `Hello.protoVersion` accepted. Default 1. */
  protoVersion?: number;
  /** Token check. Return `false` or a {@link MockAuthResult} to reject. Default: accept all. */
  authenticate?: (hello: NonNullable<ClientFrameJson['hello']>) => boolean | MockAuthResult;
  /** Extra/overriding fields merged into the generated `Welcome`. */
  welcome?: Partial<WelcomeJson>;
  /** Echo the `Hello.rid` on handshake errors. Default `true`. */
  echoHelloRid?: boolean;
}

/** One frame as received by the mock server, in arrival order. */
export interface ReceivedFrame {
  connectionId: number;
  frame: ClientFrameJson;
  /** Exact text the client put on the wire. */
  raw: string;
}

/**
 * How to select a client frame.
 *
 * - a string selects by `oneof body` case (`'hello'`, `'ping'`, `'bye'`);
 * - an object is matched as a subset (see `matchStructural`), `"…"` acting as a wildcard;
 * - a predicate is called with the decoded frame.
 */
export type FrameMatcher = string | Record<string, unknown> | ((frame: ClientFrameJson) => boolean);

/** Options for `expectFrame`. */
export interface ExpectFrameOptions {
  /** Real-time budget before giving up. Default 2000 ms. */
  timeoutMs?: number;
  /** Also consider frames received before the call. Default `true`. */
  includeBuffered?: boolean;
}

/** A live connection to the mock server. */
export interface MockConnection {
  readonly id: number;
  /** Frames this connection received, in arrival order. */
  readonly received: readonly ClientFrameJson[];
  readonly authenticated: boolean;
  readonly closed: boolean;
  /** Close code observed or sent, or `null` while open. */
  readonly closeCode: number | null;
  /** Send a raw `ServerFrame` (proto3 JSON). A missing `id` is filled with a fresh UUIDv7. */
  send(frame: ServerFrameJson): void;
  /** Send `Ack` for `rid`. */
  ack(rid: string, extra?: { id?: string; seq?: string }): void;
  /** Send an `Error` frame. */
  sendError(
    code: ErrorCodeJson,
    options?: { rid?: string; message?: string; retryAfterMs?: number },
  ): void;
  /** Send `Reconnect{delayMs}`; the connection is *not* closed automatically. */
  sendReconnect(delayMs: number, reason?: string): void;
  /** Send `ResyncRequired`. */
  sendResyncRequired(conversationIds?: string[], full?: boolean): void;
  /** Graceful close. */
  close(code?: number, reason?: string): void;
  /** Abrupt drop: the client observes close code 1006 with no close handshake. */
  drop(): void;
  /** Stop answering `Ping` (or resume, with `false`). */
  simulateSilence(silent?: boolean): void;
  /** Wait for a matching frame on this connection. */
  expectFrame(matcher: FrameMatcher, options?: ExpectFrameOptions): Promise<ClientFrameJson>;
}

/** Everything a test can do to a mock Talky server, regardless of transport. */
export interface MockServerControl {
  /** Every connection ever accepted, oldest first (closed ones included). */
  readonly connections: readonly MockConnection[];
  /** Every frame received across all connections, in arrival order. */
  readonly received: readonly ReceivedFrame[];
  /** The most recent connection. */
  latest(): MockConnection;
  /** Resolve once connection number `index` (0-based) exists. */
  waitForConnection(index?: number, timeoutMs?: number): Promise<MockConnection>;
  /** Send on the most recent connection. */
  send(frame: ServerFrameJson): void;
  /** Wait for a matching frame on any connection. */
  expectFrame(matcher: FrameMatcher, options?: ExpectFrameOptions): Promise<ClientFrameJson>;
  /** Abruptly drop the most recent connection (client sees 1006). */
  dropConnection(): void;
  /** `Reconnect{delayMs}` on the most recent connection. */
  sendReconnect(delayMs: number, reason?: string): void;
  /** Stop answering `Ping` on the most recent connection. */
  simulateSilence(silent?: boolean): void;
  /**
   * Script a reply, keyed by `rid`.
   *
   * The responder is called for every request-bearing frame that matches; returning a
   * `ServerFrameJson` sends it (a missing `rid`/`id` is filled in), returning `undefined`
   * sends nothing.
   *
   * @returns a function that removes the script.
   */
  onRequest(
    matcher: FrameMatcher,
    responder: (frame: ClientFrameJson, connection: MockConnection) => ServerFrameJson | undefined,
  ): () => void;
  /** Shut everything down. */
  close(): Promise<void>;
}
