import { create } from '@bufbuild/protobuf';
import type { Ack, ServerFrame, Welcome } from '@talky/protocol/v1';
import {
  ByeSchema,
  ClientInfoSchema,
  ErrorCode,
  HelloSchema,
  PingSchema,
} from '@talky/protocol/v1';
import { TalkyWsError } from '../errors.js';
import { createEmitter } from '../events.js';
import { noopLogger } from '../logger.js';
import { systemTimerHost } from '../time.js';
import type { TimerHandle } from '../time.js';
import { createUuidV7 } from '../uuid.js';
import { backoffDelayMs, DEFAULT_BACKOFF } from './backoff.js';
import type { ClientFrameBody } from './codec.js';
import { clientFrame, decodeServerFrame, encodeClientFrame } from './codec.js';
import { createFrameDeduper } from './dedupe.js';
import type { WebSocketLike } from './WebSocketLike.js';
import { defaultWebSocketFactory, WS_OPEN } from './WebSocketLike.js';
import type {
  ReconnectReason,
  RequestOptions,
  ResolvedWsClientOptions,
  WsClient,
  WsClientEvents,
  WsClientOptions,
  WsClientState,
} from './types.js';

/** Fallback when `Welcome.heartbeatIntervalMs` is absent or zero. */
const DEFAULT_HEARTBEAT_MS = 25_000;
/** Protocol cap on `Hello.since`. */
const MAX_SINCE_ENTRIES = 50;
/** How long to wait for the server's own close after it sent a fatal `Error`. */
const FATAL_CLOSE_GRACE_MS = 5_000;
/** How long to wait for `onclose` after *we* asked the socket to close. */
const CLOSE_WATCHDOG_MS = 3_000;
/** How long to wait for `onclose` after the socket reported `onerror`. */
const ERROR_CLOSE_WATCHDOG_MS = 3_000;
/** Synthetic close code used when a watchdog has to fabricate a close event. */
const CLOSE_ABNORMAL = 1006;
/** The only close code this client ever *sends*; the reason string carries the detail. */
const CLOSE_NORMAL = 1000;

/**
 * Close codes after which reconnecting cannot possibly help.
 *
 * 1002 (`PROTOCOL_VERSION_UNSUPPORTED`) and 1003 are permanent disagreements about the
 * contract; 1008 (`UNAUTHENTICATED` / `HELLO_REQUIRED`) is handled separately because
 * `retryOnAuthFailure` can turn it back into a retryable condition.
 */
const PERMANENT_CLOSE_CODES: ReadonlySet<number> = new Set([1002, 1003]);
const AUTH_CLOSE_CODE = 1008;
/** `Reconnect.reason` sent when `MaxConnectionsPerUser` evicted this connection. */
const EVICTION_REASON = 'too_many_connections';

type TimerName =
  | 'heartbeat'
  | 'pong'
  | 'reconnect'
  | 'handshake'
  | 'closeWatchdog'
  | 'errorWatchdog'
  | 'fatalGrace';

interface PendingRequest {
  resolve: (ack: Ack) => void;
  reject: (error: TalkyWsError) => void;
  timer: TimerHandle;
}

function resolveOptions(options: WsClientOptions): ResolvedWsClientOptions {
  return {
    url: options.url,
    getToken: options.getToken,
    deviceId: options.deviceId,
    client: options.client,
    protoVersion: options.protoVersion ?? 1,
    webSocketFactory: options.webSocketFactory ?? defaultWebSocketFactory,
    timers: options.timers ?? systemTimerHost,
    random: options.random ?? ((): number => Math.random()),
    getSince: options.getSince ?? ((): Record<string, bigint | string> => ({})),
    logger: options.logger ?? noopLogger,
    requestTimeoutMs: options.requestTimeoutMs ?? 15_000,
    pongTimeoutMs: options.pongTimeoutMs ?? 10_000,
    backoff: { ...DEFAULT_BACKOFF, ...options.backoff },
    retryOnAuthFailure: options.retryOnAuthFailure ?? false,
    dedupeCapacity: options.dedupeCapacity ?? 2048,
  };
}

class TalkyWsClient implements WsClient {
  private readonly options: ResolvedWsClientOptions;
  private readonly emitter = createEmitter<WsClientEvents>();
  private readonly deduper;
  private readonly newId: () => string;
  private readonly pending = new Map<string, PendingRequest>();
  private readonly timers = new Map<TimerName, TimerHandle>();

  private currentState: WsClientState = 'idle';
  private socket: WebSocketLike | null = null;
  /** Bumped for every connection attempt so stale socket callbacks can be ignored. */
  private connectionId = 0;
  private closedByUser = false;
  private attempt = 0;
  private heartbeatIntervalMs = DEFAULT_HEARTBEAT_MS;
  private lastSentAt = 0;
  private lastRttMs: number | null = null;
  private currentWelcome: Welcome | null = null;
  /** Set when the server told us it is draining; overrides the backoff for one attempt. */
  private serverReconnectDelayMs: number | null = null;
  /** Set when a fatal `Error` frame arrived and we are waiting for the server to close. */
  private fatalPending = false;
  /** Set when the server evicted this connection in favour of a newer one of the same user. */
  private evictionReason: string | null = null;

  constructor(options: WsClientOptions) {
    this.options = resolveOptions(options);
    this.deduper = createFrameDeduper(this.options.dedupeCapacity);
    this.newId = createUuidV7({
      now: () => this.options.timers.now(),
      random: this.options.random,
    });
  }

  // ---------------------------------------------------------------------------
  // Public surface
  // ---------------------------------------------------------------------------

  get state(): WsClientState {
    return this.currentState;
  }

  get rttMs(): number | null {
    return this.lastRttMs;
  }

  get welcome(): Welcome | null {
    return this.currentWelcome;
  }

  on: WsClient['on'] = (event, listener) => this.emitter.on(event, listener);
  once: WsClient['once'] = (event, listener) => this.emitter.once(event, listener);
  off: WsClient['off'] = (event, listener) => {
    this.emitter.off(event, listener);
  };
  listenerCount: WsClient['listenerCount'] = (event) => this.emitter.listenerCount(event);

  connect(): void {
    this.closedByUser = false;
    if (
      this.currentState === 'connecting' ||
      this.currentState === 'authenticating' ||
      this.currentState === 'open'
    ) {
      return;
    }
    this.clearTimer('reconnect');
    this.attempt = 0;
    void this.openConnection();
  }

  close(reason = 'client-close'): void {
    if (this.currentState === 'closed') return;
    this.closedByUser = true;
    this.clearTimer('reconnect');
    this.clearTimer('fatalGrace');
    this.serverReconnectDelayMs = null;

    const socket = this.socket;
    if (socket === null) {
      this.rejectPending(new TalkyWsError({ reason: 'closed', message: 'client closed' }));
      this.setState('closed');
      return;
    }
    if (socket.readyState === WS_OPEN) {
      try {
        this.writeFrame(clientFrame({ case: 'bye', value: create(ByeSchema, {}) }));
      } catch (error) {
        this.options.logger.debug('bye could not be sent', { error });
      }
    }
    this.initiateClose(reason);
  }

  send(body: ClientFrameBody): void {
    this.writeFrame(clientFrame(body));
  }

  request(body: ClientFrameBody, requestOptions?: RequestOptions): Promise<Ack> {
    const timeoutMs = requestOptions?.timeoutMs ?? this.options.requestTimeoutMs;
    const rid = this.newId();
    return new Promise<Ack>((resolve, reject) => {
      if (this.currentState !== 'open') {
        reject(
          new TalkyWsError({
            reason: this.closedByUser ? 'closed' : 'disconnected',
            message: `cannot send a request while the client is '${this.currentState}'`,
            rid,
          }),
        );
        return;
      }
      const timer = this.options.timers.setTimeout(() => {
        this.pending.delete(rid);
        reject(
          new TalkyWsError({
            reason: 'timeout',
            message: `no reply within ${timeoutMs.toString()} ms`,
            rid,
          }),
        );
      }, timeoutMs);
      this.pending.set(rid, { resolve, reject, timer });
      try {
        this.writeFrame(clientFrame(body, rid));
      } catch (error) {
        this.pending.delete(rid);
        this.options.timers.clearTimeout(timer);
        reject(
          error instanceof TalkyWsError
            ? error
            : new TalkyWsError({ reason: 'transport', message: 'send failed', rid, cause: error }),
        );
      }
    });
  }

  // ---------------------------------------------------------------------------
  // Connection lifecycle
  // ---------------------------------------------------------------------------

  private async openConnection(): Promise<void> {
    if (this.closedByUser) return;
    this.setState('connecting');
    this.connectionId += 1;
    const id = this.connectionId;

    let token: string;
    try {
      token = await this.options.getToken();
    } catch (error) {
      if (this.isSuperseded(id)) return;
      this.emitError(
        new TalkyWsError({ reason: 'transport', message: 'getToken() rejected', cause: error }),
      );
      this.scheduleBackoffReconnect('token', undefined);
      return;
    }
    if (this.isSuperseded(id)) return;

    let socket: WebSocketLike;
    try {
      socket = this.options.webSocketFactory(this.options.url);
    } catch (error) {
      this.emitError(
        new TalkyWsError({ reason: 'transport', message: 'could not open a socket', cause: error }),
      );
      this.scheduleBackoffReconnect('transport', undefined);
      return;
    }

    this.socket = socket;
    socket.onopen = () => {
      this.handleSocketOpen(id, token);
    };
    socket.onmessage = (event) => {
      this.handleSocketMessage(id, event.data);
    };
    socket.onclose = (event) => {
      this.handleClose(id, event.code, event.reason);
    };
    socket.onerror = (event) => {
      this.handleSocketError(id, event);
    };
  }

  private handleSocketOpen(id: number, token: string): void {
    if (this.connectionId !== id) return;
    this.setState('authenticating');
    const rid = this.newId();
    try {
      this.writeFrame(
        clientFrame(
          {
            case: 'hello',
            value: create(HelloSchema, {
              protoVersion: this.options.protoVersion,
              token,
              deviceId: this.options.deviceId,
              client: create(ClientInfoSchema, this.options.client),
              since: this.buildSince(),
            }),
          },
          rid,
        ),
      );
    } catch (error) {
      this.emitError(
        new TalkyWsError({ reason: 'transport', message: 'Hello could not be sent', cause: error }),
      );
      this.initiateClose('hello-failed');
      return;
    }
    // The server answers a valid Hello with Welcome. If nothing arrives the link is useless.
    this.setTimer(
      'handshake',
      () => {
        if (this.currentState !== 'authenticating') return;
        this.emitError(
          new TalkyWsError({
            reason: 'timeout',
            message: `no Welcome within ${this.options.requestTimeoutMs.toString()} ms`,
          }),
        );
        this.initiateClose('handshake-timeout');
      },
      this.options.requestTimeoutMs,
    );
  }

  private handleSocketError(id: number, event: unknown): void {
    if (this.connectionId !== id) return;
    this.emitError(
      new TalkyWsError({ reason: 'transport', message: 'socket error', cause: event }),
    );
    // Every conforming implementation fires `close` right after `error`; the watchdog covers
    // the ones that do not, so the client can never get stuck without a live connection.
    this.setTimer(
      'errorWatchdog',
      () => {
        this.handleClose(id, CLOSE_ABNORMAL, 'error-without-close');
      },
      ERROR_CLOSE_WATCHDOG_MS,
    );
  }

  private handleSocketMessage(id: number, data: unknown): void {
    if (this.connectionId !== id) return;
    if (typeof data !== 'string') {
      this.emitError(
        new TalkyWsError({
          reason: 'decode',
          message: 'protocol v1 carries proto3 JSON in text frames; got a non-string payload',
        }),
      );
      return;
    }
    let frame: ServerFrame;
    try {
      frame = decodeServerFrame(data);
    } catch (error) {
      this.emitError(
        new TalkyWsError({ reason: 'decode', message: 'malformed server frame', cause: error }),
      );
      return;
    }
    this.handleFrame(frame);
  }

  private handleFrame(frame: ServerFrame): void {
    if (!this.deduper.acceptId(frame.id)) {
      this.options.logger.debug('dropping duplicate frame', { id: frame.id });
      return;
    }
    this.emitSafe('frame', frame);

    switch (frame.body.case) {
      case 'welcome':
        this.handleWelcome(frame.body.value);
        return;
      case 'pong': {
        this.clearTimer('pong');
        const rtt = this.options.timers.now() - Number(frame.body.value.ts);
        this.lastRttMs = rtt;
        this.emitSafe('rtt', rtt);
        return;
      }
      case 'ack':
        this.handleAck(frame.body.value);
        return;
      case 'error':
        this.handleErrorFrame(frame.body.value);
        return;
      case 'resyncRequired':
        this.emitSafe('resyncRequired', frame.body.value);
        return;
      case 'reconnect':
        this.handleReconnectFrame(frame.body.value.delayMs, frame.body.value.reason);
        return;
      case undefined:
        this.emitError(
          new TalkyWsError({
            reason: 'protocol',
            code: ErrorCode.INVALID_FRAME,
            message: 'server frame carried no known body',
          }),
        );
        return;
    }
  }

  private handleWelcome(welcome: Welcome): void {
    this.clearTimer('handshake');
    this.currentWelcome = welcome;
    this.heartbeatIntervalMs =
      welcome.heartbeatIntervalMs > 0 ? welcome.heartbeatIntervalMs : DEFAULT_HEARTBEAT_MS;
    // A completed handshake is the only thing that proves the endpoint is healthy, so it is
    // the only thing that resets the backoff.
    this.attempt = 0;
    this.setState('open');
    this.emitSafe('welcome', welcome);
    this.emitSafe('open');
    this.scheduleHeartbeat();
  }

  private handleAck(ack: Ack): void {
    const entry = this.pending.get(ack.rid);
    if (entry === undefined) {
      this.options.logger.debug('Ack for an unknown rid', { rid: ack.rid });
      return;
    }
    this.pending.delete(ack.rid);
    this.options.timers.clearTimeout(entry.timer);
    entry.resolve(ack);
  }

  private handleErrorFrame(frame: {
    rid: string;
    code: ErrorCode;
    message: string;
    retryAfterMs: number;
  }): void {
    const error = new TalkyWsError({
      reason: 'protocol',
      code: frame.code,
      message: frame.message === '' ? `server error ${ErrorCode[frame.code]}` : frame.message,
      rid: frame.rid,
      retryAfterMs: frame.retryAfterMs === 0 ? undefined : frame.retryAfterMs,
    });

    const entry = frame.rid === '' ? undefined : this.pending.get(frame.rid);
    if (entry !== undefined) {
      this.pending.delete(frame.rid);
      this.options.timers.clearTimeout(entry.timer);
      entry.reject(error);
      return;
    }

    this.emitError(error);

    // A handshake rejection is terminal: the server closes right after, and retrying with the
    // same token or protocol version would just spin. We follow the server's close instead of
    // racing it, but arm a grace timer in case it never arrives.
    if (this.isFatalErrorCode(frame.code)) {
      this.fatalPending = true;
      this.setTimer(
        'fatalGrace',
        () => {
          this.initiateClose('protocol-error');
        },
        FATAL_CLOSE_GRACE_MS,
      );
    }
  }

  private handleReconnectFrame(delayMs: number, reason: string): void {
    if (reason === EVICTION_REASON) {
      // `docs/protocol.md`: a newer connection of the same user is live. Reconnecting would
      // evict *that* one and the two devices would take turns forever. Stop and tell the app.
      this.evictionReason = reason;
      this.options.logger.warn('server evicted this connection', { reason });
      this.initiateClose('evicted');
      return;
    }
    // `docs/protocol.md`: reconnect after a random delay in [0, delayMs] so a draining node
    // does not get a thundering herd back.
    const delay = Math.max(0, Math.floor(this.options.random() * delayMs));
    this.serverReconnectDelayMs = delay;
    this.options.logger.info('server asked for a reconnect', { delayMs, delay, reason });
    this.initiateClose('server-reconnect');
  }

  private isFatalErrorCode(code: ErrorCode): boolean {
    if (code === ErrorCode.PROTOCOL_VERSION_UNSUPPORTED) return true;
    if (code === ErrorCode.UNAUTHENTICATED || code === ErrorCode.HELLO_REQUIRED) {
      return !this.options.retryOnAuthFailure;
    }
    return false;
  }

  /** Whether this connection attempt has been overtaken by a newer one, or by `close()`. */
  private isSuperseded(id: number): boolean {
    return this.connectionId !== id || this.closedByUser;
  }

  private isPermanentCloseCode(code: number): boolean {
    if (PERMANENT_CLOSE_CODES.has(code)) return true;
    return code === AUTH_CLOSE_CODE && !this.options.retryOnAuthFailure;
  }

  private handleClose(id: number, code: number, reason: string): void {
    if (this.connectionId !== id) return;
    this.clearTimer('closeWatchdog');
    this.clearTimer('errorWatchdog');
    this.clearTimer('heartbeat');
    this.clearTimer('pong');
    this.clearTimer('handshake');
    this.clearTimer('fatalGrace');
    this.detachSocket();
    this.currentWelcome = null;

    const wasFatalFrame = this.fatalPending;
    this.fatalPending = false;

    this.rejectPending(
      new TalkyWsError({
        reason: this.closedByUser ? 'closed' : 'disconnected',
        message: `connection closed (${code.toString()}${reason === '' ? '' : `: ${reason}`})`,
        closeCode: code,
      }),
    );

    if (this.closedByUser) {
      this.setState('closed');
      return;
    }

    const evictionReason = this.evictionReason;
    this.evictionReason = null;
    if (evictionReason !== null) {
      this.options.logger.warn('connection evicted; not reconnecting', { code, evictionReason });
      this.setState('closed');
      this.emitSafe('evicted', evictionReason);
      this.emitError(
        new TalkyWsError({
          reason: 'evicted',
          message: `server evicted this connection (${evictionReason}); a newer connection of the same user is live`,
          closeCode: code,
        }),
      );
      return;
    }

    if (wasFatalFrame || this.isPermanentCloseCode(code)) {
      if (!wasFatalFrame) {
        this.emitError(
          new TalkyWsError({
            reason: 'protocol',
            message: `server closed the connection with code ${code.toString()}; not retrying`,
            closeCode: code,
          }),
        );
      }
      this.options.logger.warn('connection closed permanently', { code, reason });
      this.setState('closed');
      return;
    }

    const serverDelay = this.serverReconnectDelayMs;
    this.serverReconnectDelayMs = null;
    if (serverDelay !== null) {
      this.armReconnect(serverDelay, 'server-reconnect', code);
      return;
    }
    this.scheduleBackoffReconnect(code === CLOSE_ABNORMAL ? 'transport' : 'close', code);
  }

  private scheduleBackoffReconnect(reason: ReconnectReason, closeCode: number | undefined): void {
    const delay = backoffDelayMs(this.attempt, this.options.backoff, this.options.random);
    this.attempt += 1;
    this.armReconnect(delay, reason, closeCode);
  }

  private armReconnect(
    delayMs: number,
    reason: ReconnectReason,
    closeCode: number | undefined,
  ): void {
    this.setState('reconnecting');
    this.emitSafe('reconnectScheduled', { attempt: this.attempt, delayMs, reason, closeCode });
    this.setTimer(
      'reconnect',
      () => {
        void this.openConnection();
      },
      delayMs,
    );
  }

  /**
   * Ask the socket to close. The client only ever sends close code 1000; `reason` carries the
   * detail (`client-close`, `pong-timeout`, `server-reconnect`, `protocol-error`, …).
   */
  private initiateClose(reason: string): void {
    const socket = this.socket;
    const id = this.connectionId;
    if (socket === null) {
      this.handleClose(id, CLOSE_NORMAL, reason);
      return;
    }
    try {
      socket.close(CLOSE_NORMAL, reason);
    } catch (error) {
      this.options.logger.debug('socket.close() threw', { error });
    }
    this.setTimer(
      'closeWatchdog',
      () => {
        this.handleClose(id, CLOSE_ABNORMAL, `${reason}:close-watchdog`);
      },
      CLOSE_WATCHDOG_MS,
    );
  }

  private detachSocket(): void {
    const socket = this.socket;
    if (socket === null) return;
    socket.onopen = null;
    socket.onmessage = null;
    socket.onclose = null;
    socket.onerror = null;
    this.socket = null;
  }

  // ---------------------------------------------------------------------------
  // Heartbeat
  // ---------------------------------------------------------------------------

  private scheduleHeartbeat(): void {
    this.setTimer(
      'heartbeat',
      () => {
        this.onHeartbeatTick();
      },
      this.heartbeatIntervalMs,
    );
  }

  private onHeartbeatTick(): void {
    if (this.currentState !== 'open') return;
    const now = this.options.timers.now();
    // `docs/protocol.md`: a frame already sent inside the window counts as the ping.
    if (now - this.lastSentAt < this.heartbeatIntervalMs) {
      this.scheduleHeartbeat();
      return;
    }
    try {
      this.writeFrame(
        clientFrame({ case: 'ping', value: create(PingSchema, { ts: BigInt(Math.trunc(now)) }) }),
      );
    } catch (error) {
      this.options.logger.debug('ping could not be sent', { error });
      return;
    }
    this.setTimer(
      'pong',
      () => {
        this.onPongTimeout();
      },
      this.options.pongTimeoutMs,
    );
    this.scheduleHeartbeat();
  }

  private onPongTimeout(): void {
    this.emitError(
      new TalkyWsError({
        reason: 'heartbeat',
        message: `no Pong within ${this.options.pongTimeoutMs.toString()} ms; treating the link as dead`,
      }),
    );
    this.initiateClose('pong-timeout');
  }

  // ---------------------------------------------------------------------------
  // Plumbing
  // ---------------------------------------------------------------------------

  private buildSince(): Record<string, bigint> {
    const raw = this.options.getSince();
    const since: Record<string, bigint> = {};
    let count = 0;
    for (const [conversationId, value] of Object.entries(raw)) {
      if (count >= MAX_SINCE_ENTRIES) {
        this.options.logger.warn('since cursor truncated to the protocol maximum', {
          limit: MAX_SINCE_ENTRIES,
        });
        break;
      }
      try {
        since[conversationId] = typeof value === 'bigint' ? value : BigInt(value);
        count += 1;
      } catch (error) {
        this.options.logger.warn('dropping unparsable since entry', { conversationId, error });
      }
    }
    return since;
  }

  private writeFrame(frame: Parameters<typeof encodeClientFrame>[0]): void {
    const socket = this.socket;
    if (socket?.readyState !== WS_OPEN) {
      throw new TalkyWsError({
        reason: this.closedByUser ? 'closed' : 'disconnected',
        message: `cannot send while the client is '${this.currentState}'`,
      });
    }
    socket.send(encodeClientFrame(frame));
    this.lastSentAt = this.options.timers.now();
  }

  private rejectPending(error: TalkyWsError): void {
    if (this.pending.size === 0) return;
    const entries = [...this.pending.values()];
    this.pending.clear();
    for (const entry of entries) {
      this.options.timers.clearTimeout(entry.timer);
      entry.reject(error);
    }
  }

  private setState(next: WsClientState): void {
    if (this.currentState === next) return;
    const previous = this.currentState;
    this.currentState = next;
    this.emitSafe('stateChange', next, previous);
  }

  private setTimer(name: TimerName, handler: () => void, delayMs: number): void {
    this.clearTimer(name);
    const handle = this.options.timers.setTimeout(() => {
      this.timers.delete(name);
      handler();
    }, delayMs);
    this.timers.set(name, handle);
  }

  private clearTimer(name: TimerName): void {
    const handle = this.timers.get(name);
    if (handle === undefined) return;
    this.timers.delete(name);
    this.options.timers.clearTimeout(handle);
  }

  /** Emit without ever letting an app listener throw into the socket loop. */
  private emitSafe<K extends keyof WsClientEvents & string>(
    event: K,
    ...args: WsClientEvents[K]
  ): void {
    const thrown = this.emitter.emit(event, ...args);
    for (const cause of thrown) {
      if (event === 'error') {
        // Never recurse: an `error` listener that throws is only logged.
        this.options.logger.error('an error listener threw', { cause });
        continue;
      }
      this.emitError(
        new TalkyWsError({
          reason: 'handler',
          message: `a listener for '${event}' threw`,
          cause,
        }),
      );
    }
  }

  private emitError(error: TalkyWsError): void {
    if (this.emitter.listenerCount('error') === 0) {
      this.options.logger.warn('unobserved client error', {
        reason: error.reason,
        message: error.message,
      });
      return;
    }
    this.emitSafe('error', error);
  }
}

/**
 * Create a Talky realtime client.
 *
 * The client is inert until {@link WsClient.connect} is called.
 *
 * ```ts
 * const client = createWsClient({
 *   url: 'wss://talky.local/api/v1/ws',
 *   getToken: async () => session.token,
 *   deviceId,
 *   client: { platform: 'web', appVersion: '0.1.0', os: 'Windows 11', locale: 'tr-TR' },
 *   getSince: () => store.lastAppliedSeqByConversation(),
 * });
 * client.on('resyncRequired', (r) => void sync.backfill(r));
 * client.connect();
 * ```
 */
export function createWsClient(options: WsClientOptions): WsClient {
  return new TalkyWsClient(options);
}
