import type {
  AckJson,
  ClientFrameJson,
  ErrorCodeJson,
  PongJson,
  ServerFrameJson,
  WelcomeJson,
} from '@talky/protocol/v1';

/**
 * Frame factories.
 *
 * Everything is built as proto3 JSON (`*Json` types), because that *is* the v1 wire format:
 * one WebSocket text frame carries exactly one of these objects. `int64` fields are strings,
 * per proto3 JSON.
 */

let counterState = { ms: -1, counter: 0 };

/**
 * Minimal UUIDv7 generator.
 *
 * `@talky/test-utils` cannot import `@talky/core` (that would make the workspace task graph
 * cyclic), so the handful of ids the mock server mints are produced here.
 */
export function testUuidV7(nowMs: number = Date.now()): string {
  const ms = Math.max(0, Math.trunc(nowMs));
  if (ms > counterState.ms) {
    counterState = { ms, counter: Math.floor(Math.random() * 0x800) };
  } else {
    counterState = { ms: counterState.ms, counter: counterState.counter + 1 };
  }
  const hex = (value: number, digits: number): string => value.toString(16).padStart(digits, '0');
  const stamp = counterState.ms;
  return [
    hex(Math.floor(stamp / 0x10000), 8),
    hex(stamp % 0x10000, 4),
    `7${hex(counterState.counter & 0xfff, 3)}`,
    hex(0x8000 | Math.floor(Math.random() * 0x4000), 4),
    hex(Math.floor(Math.random() * 0x1000000), 6) + hex(Math.floor(Math.random() * 0x1000000), 6),
  ].join('-');
}

/** RFC 3339 timestamp as proto3 JSON encodes `google.protobuf.Timestamp`. */
export function timestampJson(epochMs: number): string {
  return new Date(epochMs).toISOString().replace(/\.(\d{3})Z$/, '.$1000000Z');
}

/** `Welcome` server frame. */
export function welcomeFrame(overrides: Partial<WelcomeJson> = {}, id?: string): ServerFrameJson {
  return {
    id: id ?? testUuidV7(),
    welcome: {
      sessionId: 'session-1',
      userId: 'u1',
      nodeId: 'node-1',
      heartbeatIntervalMs: 25_000,
      serverVersion: 'mock-0.1.0',
      ...overrides,
    },
  };
}

/** `Pong` echoing `ts` exactly, as the protocol requires for RTT measurement. */
export function pongFrame(
  ts: string,
  overrides: Partial<PongJson> = {},
  id?: string,
): ServerFrameJson {
  return { id: id ?? testUuidV7(), pong: { ts, ...overrides } };
}

/** `Ack` for a request `rid`. */
export function ackFrame(
  rid: string,
  extra: Omit<AckJson, 'rid'> = {},
  id?: string,
): ServerFrameJson {
  return { id: id ?? testUuidV7(), ack: { rid, ...extra } };
}

/** `Error` frame. `rid` is omitted for errors that are not tied to a request. */
export function errorFrame(
  code: ErrorCodeJson,
  options: { rid?: string; message?: string; retryAfterMs?: number } = {},
  id?: string,
): ServerFrameJson {
  const error: NonNullable<ServerFrameJson['error']> = { code };
  if (options.rid !== undefined) error.rid = options.rid;
  if (options.message !== undefined) error.message = options.message;
  if (options.retryAfterMs !== undefined) error.retryAfterMs = options.retryAfterMs;
  return { id: id ?? testUuidV7(), error };
}

/** `Reconnect{delayMs}` — the drain signal. */
export function reconnectFrame(delayMs: number, reason = 'draining', id?: string): ServerFrameJson {
  return { id: id ?? testUuidV7(), reconnect: { delayMs, reason } };
}

/** `ResyncRequired`. */
export function resyncRequiredFrame(
  conversationIds: string[] = [],
  full = false,
  id?: string,
): ServerFrameJson {
  return { id: id ?? testUuidV7(), resyncRequired: { conversationIds, full } };
}

/** The `oneof body` case of a client frame, or `undefined` when none is set. */
export function clientFrameCase(frame: ClientFrameJson): 'hello' | 'ping' | 'bye' | undefined {
  if (frame.hello !== undefined) return 'hello';
  if (frame.ping !== undefined) return 'ping';
  if (frame.bye !== undefined) return 'bye';
  return undefined;
}
