import type { ErrorCode } from '@talky/protocol/v1';

/**
 * Why a {@link TalkyWsError} was raised. Apps switch on this rather than on message text.
 *
 * - `protocol` — the server sent a `talky.v1.Error` frame, or violated the frame contract.
 * - `timeout` — no `Ack`/`Error` arrived for a request within its timeout.
 * - `disconnected` — the connection dropped while the request was in flight, or a frame was
 *   handed to a client that is not currently open.
 * - `closed` — the client was closed by the app while the request was in flight.
 * - `transport` — the socket itself failed (DNS, TLS, refused, `onerror`), or `getToken()`
 *   rejected.
 * - `decode` — a server frame could not be parsed as proto3 JSON.
 * - `heartbeat` — no `Pong` arrived inside `pongTimeoutMs`; the link is treated as dead.
 * - `handler` — an app-supplied event listener threw. The socket loop swallows it and
 *   reports it here instead of tearing the connection down.
 */
export type TalkyWsErrorReason =
  | 'protocol'
  | 'timeout'
  | 'disconnected'
  | 'closed'
  | 'transport'
  | 'decode'
  | 'heartbeat'
  | 'handler'
  /** The server evicted this connection (`Reconnect{reason:"too_many_connections"}`); no retry. */
  | 'evicted';

/** Constructor payload for {@link TalkyWsError}. */
export interface TalkyWsErrorInit {
  reason: TalkyWsErrorReason;
  message?: string | undefined;
  /** `talky.v1.ErrorCode` when the error came from a server `Error` frame. */
  code?: ErrorCode | undefined;
  /** Correlation id of the request this error belongs to, when there is one. */
  rid?: string | undefined;
  /** Server retry hint (`RATE_LIMITED` / `UNAVAILABLE`). */
  retryAfterMs?: number | undefined;
  /** WebSocket close code, when the error was raised while handling a close. */
  closeCode?: number | undefined;
  cause?: unknown;
}

/**
 * The single error type `@talky/core` rejects and emits with.
 *
 * Every field except {@link TalkyWsError.reason} may be `undefined`; `reason` is always a
 * member of {@link TalkyWsErrorReason} so callers can exhaustively switch on it.
 */
export class TalkyWsError extends Error {
  override readonly name = 'TalkyWsError';
  readonly reason: TalkyWsErrorReason;
  readonly code: ErrorCode | undefined;
  readonly rid: string | undefined;
  readonly retryAfterMs: number | undefined;
  readonly closeCode: number | undefined;

  constructor(init: TalkyWsErrorInit) {
    super(
      init.message ?? init.reason,
      init.cause === undefined ? undefined : { cause: init.cause },
    );
    this.reason = init.reason;
    this.code = init.code;
    this.rid = init.rid;
    this.retryAfterMs = init.retryAfterMs;
    this.closeCode = init.closeCode;
  }
}

/** Type guard for {@link TalkyWsError}, safe across bundle boundaries. */
export function isTalkyWsError(value: unknown): value is TalkyWsError {
  return value instanceof TalkyWsError;
}
