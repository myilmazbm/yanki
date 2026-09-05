import type { Ack, ResyncRequired, ServerFrame, Welcome } from '@talky/protocol/v1';
import type { TalkyWsError } from '../errors.js';
import type { EventSubscriber } from '../events.js';
import type { Logger } from '../logger.js';
import type { TimerHost } from '../time.js';
import type { BackoffOptions } from './backoff.js';
import type { ClientFrameBody } from './codec.js';
import type { WebSocketFactory } from './WebSocketLike.js';

/**
 * Connection lifecycle.
 *
 * ```text
 * idle ──connect()──► connecting ──socket open──► authenticating ──Welcome──► open
 *   ▲                     ▲                             │                      │
 *   │                     └────────── timer ────── reconnecting ◄──── drop ────┘
 *   └──────────────────── close() / fatal ──────────► closed
 * ```
 */
export type WsClientState =
  'idle' | 'connecting' | 'authenticating' | 'open' | 'reconnecting' | 'closed';

/** `ClientInfo.platform` values understood by the server. */
export type ClientPlatform = 'web' | 'desktop' | 'ios' | 'android';

/** Static description of the host application, sent in `Hello.client`. */
export interface ClientDescriptor {
  platform: ClientPlatform;
  appVersion: string;
  os: string;
  /** BCP-47 tag, e.g. `tr-TR`. */
  locale: string;
}

/** Why a reconnect was scheduled. */
export type ReconnectReason =
  'close' | 'transport' | 'heartbeat' | 'server-reconnect' | 'token' | 'handshake-timeout';

/** Payload of the `reconnectScheduled` event. */
export interface ReconnectScheduled {
  /** 1-based retry number within the current backoff run; `0` for a server-driven reconnect. */
  attempt: number;
  /** Delay actually chosen, jitter included. */
  delayMs: number;
  reason: ReconnectReason;
  /** WebSocket close code that triggered it, when the trigger was a close. */
  closeCode: number | undefined;
}

/** Events emitted by a {@link WsClient}. */
export interface WsClientEvents extends Record<string, unknown[]> {
  /** Session is usable: `Welcome` has been received and `state` is `'open'`. */
  open: [];
  /** The `Welcome` payload, emitted immediately before `open`. */
  welcome: [welcome: Welcome];
  /** Every server frame that survived de-duplication, before it is routed internally. */
  frame: [frame: ServerFrame];
  /** Any error that is not the rejection of a specific in-flight request. */
  error: [error: TalkyWsError];
  /** Fired on every transition; `previous` is the state being left. */
  stateChange: [state: WsClientState, previous: WsClientState];
  /** The server could not replay the gap — the app must call `GET /api/v1/sync`. */
  resyncRequired: [resync: ResyncRequired];
  /** A reconnect attempt has been armed. */
  reconnectScheduled: [info: ReconnectScheduled];
  /**
   * The server evicted this connection because a newer connection of the same user is live
   * (`Reconnect{reason:"too_many_connections"}`). The client is `closed` and will not retry;
   * the app decides (typically "signed in on another device").
   */
  evicted: [reason: string];
  /** A fresh round-trip-time sample, in milliseconds. */
  rtt: [rttMs: number];
}

/** Options for {@link createWsClient}. */
export interface WsClientOptions {
  /** Absolute `ws://` / `wss://` endpoint, e.g. `wss://talky.local/api/v1/ws`. */
  url: string;
  /**
   * Resolves the session token for `Hello.token`. Called before **every** connection attempt,
   * so a refreshed token is picked up automatically on reconnect. Cookie-authenticated web
   * clients may resolve an empty string.
   */
  getToken: () => Promise<string>;
  /** Stable per-install device id (UUID). */
  deviceId: string;
  client: ClientDescriptor;
  /** Protocol major version. Only `1` exists today. */
  protoVersion?: 1;
  /** Transport factory. Defaults to the ambient global `WebSocket`. */
  webSocketFactory?: WebSocketFactory;
  /** Clock seam. Defaults to `setTimeout`/`Date.now`. */
  timers?: TimerHost;
  /** Uniform random source in `[0, 1)`. Defaults to `Math.random`. */
  random?: () => number;
  /**
   * Resume cursor: `conversationId → last applied seq`. Called when building each `Hello`.
   * At most 50 entries are sent (protocol limit); the rest is reconciled over REST.
   */
  getSince?: () => Record<string, bigint | string>;
  logger?: Logger;
  /** Default timeout for {@link WsClient.request}. Default 15 000 ms. */
  requestTimeoutMs?: number;
  /** How long to wait for a `Pong` before declaring the link dead. Default 10 000 ms. */
  pongTimeoutMs?: number;
  /** Reconnect backoff. Defaults to the protocol values (0.5 s → ×2 → 30 s, ±20 %). */
  backoff?: Partial<BackoffOptions>;
  /**
   * Keep retrying after the server rejected the handshake with close code 1008
   * (`UNAUTHENTICATED` / `HELLO_REQUIRED`). Default `false`: a bad token is not something a
   * retry loop can fix, so the client stops and lets the app re-authenticate.
   */
  retryOnAuthFailure?: boolean;
  /** LRU capacity of the built-in `id` de-duplicator. Default 2048. */
  dedupeCapacity?: number;
}

/** Per-call overrides for {@link WsClient.request}. */
export interface RequestOptions {
  /** Overrides `WsClientOptions.requestTimeoutMs` for this call. */
  timeoutMs?: number;
}

/**
 * Talky realtime client.
 *
 * One instance owns one logical session: it connects, performs the `Hello`/`Welcome`
 * handshake, keeps the link alive, reconnects with backoff and resumes with a `since` cursor.
 */
export interface WsClient extends EventSubscriber<WsClientEvents> {
  /** Current lifecycle state. */
  readonly state: WsClientState;
  /** Most recent round-trip-time sample in milliseconds, or `null` before the first `Pong`. */
  readonly rttMs: number | null;
  /** The `Welcome` of the current session, or `null` while not open. */
  readonly welcome: Welcome | null;

  /**
   * Start connecting (and re-enable reconnection after a {@link WsClient.close}).
   *
   * Idempotent while already connecting or open. When called during a scheduled reconnect it
   * cancels the timer and attempts immediately.
   */
  connect(): void;

  /**
   * Send `Bye`, close with code 1000 and disable reconnection.
   *
   * In-flight requests reject with `reason: 'closed'`.
   */
  close(reason?: string): void;

  /**
   * Fire-and-forget send. The frame carries an empty `rid`.
   *
   * @throws {TalkyWsError} with `reason: 'disconnected'` when the session is not open.
   */
  send(body: ClientFrameBody): void;

  /**
   * Send a frame with a fresh UUIDv7 `rid` and wait for its reply.
   *
   * Resolves with the matching `Ack`; rejects with a `TalkyWsError` on a matching `Error`
   * frame (`reason: 'protocol'`), on timeout (`reason: 'timeout'`), or when the connection
   * drops first (`reason: 'disconnected'`).
   */
  request(body: ClientFrameBody, options?: RequestOptions): Promise<Ack>;
}

/** Resolved, non-optional view of {@link WsClientOptions}. */
export interface ResolvedWsClientOptions {
  url: string;
  getToken: () => Promise<string>;
  deviceId: string;
  client: ClientDescriptor;
  protoVersion: number;
  webSocketFactory: WebSocketFactory;
  timers: TimerHost;
  random: () => number;
  getSince: () => Record<string, bigint | string>;
  logger: Logger;
  requestTimeoutMs: number;
  pongTimeoutMs: number;
  backoff: BackoffOptions;
  retryOnAuthFailure: boolean;
  dedupeCapacity: number;
}

/** Re-exported so callers can name the resolved `Ack` type without a protocol import. */
export type { Ack };
