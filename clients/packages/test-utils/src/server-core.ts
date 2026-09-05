import { fromJsonString } from '@bufbuild/protobuf';
import type { ClientFrameJson, ErrorCodeJson, ServerFrameJson } from '@talky/protocol/v1';
import { ClientFrameSchema } from '@talky/protocol/v1';
import {
  ackFrame,
  clientFrameCase,
  errorFrame,
  pongFrame,
  reconnectFrame,
  resyncRequiredFrame,
  testUuidV7,
  timestampJson,
  welcomeFrame,
} from './frames.js';
import { describeMatcher, frameMatches } from './match.js';
import type {
  ExpectFrameOptions,
  FrameMatcher,
  MockAuthResult,
  MockConnection,
  MockServerControl,
  MockServerOptions,
  ReceivedFrame,
} from './types.js';

/** What a transport must provide for one accepted connection. */
export interface ConnectionSink {
  sendText(text: string): void;
  /** Graceful close handshake. */
  closeSocket(code: number, reason: string): void;
  /** Abrupt drop — the peer observes close code 1006. */
  dropSocket(): void;
}

interface ResolvedOptions {
  autoWelcome: boolean;
  autoPong: boolean;
  autoAck: boolean;
  heartbeatIntervalMs: number;
  protoVersion: number;
  authenticate: (hello: NonNullable<ClientFrameJson['hello']>) => boolean | MockAuthResult;
  welcome: Partial<NonNullable<ServerFrameJson['welcome']>>;
  echoHelloRid: boolean;
}

interface Script {
  matcher: FrameMatcher;
  responder: (frame: ClientFrameJson, connection: MockConnection) => ServerFrameJson | undefined;
}

interface FrameWaiter {
  matcher: FrameMatcher;
  connectionId: number | null;
  resolve: (frame: ClientFrameJson) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

const DEFAULT_EXPECT_TIMEOUT_MS = 2_000;

function resolveOptions(options: MockServerOptions): ResolvedOptions {
  return {
    autoWelcome: options.autoWelcome ?? true,
    autoPong: options.autoPong ?? true,
    autoAck: options.autoAck ?? false,
    heartbeatIntervalMs: options.heartbeatIntervalMs ?? 25_000,
    protoVersion: options.protoVersion ?? 1,
    authenticate: options.authenticate ?? ((): boolean => true),
    welcome: options.welcome ?? {},
    echoHelloRid: options.echoHelloRid ?? true,
  };
}

/** One accepted connection. */
export class MockSession implements MockConnection {
  readonly id: number;
  readonly received: ClientFrameJson[] = [];
  authenticated = false;
  closed = false;
  closeCode: number | null = null;
  silent = false;

  constructor(
    id: number,
    private readonly sink: ConnectionSink,
    private readonly core: MockServerCore,
    private readonly options: ResolvedOptions,
  ) {
    this.id = id;
  }

  send(frame: ServerFrameJson): void {
    if (this.closed) return;
    const withId: ServerFrameJson = frame.id === undefined ? { ...frame, id: testUuidV7() } : frame;
    this.sink.sendText(JSON.stringify(withId));
  }

  ack(rid: string, extra: { id?: string; seq?: string } = {}): void {
    this.send(ackFrame(rid, extra));
  }

  sendError(
    code: ErrorCodeJson,
    options: { rid?: string; message?: string; retryAfterMs?: number } = {},
  ): void {
    this.send(errorFrame(code, options));
  }

  sendReconnect(delayMs: number, reason = 'draining'): void {
    this.send(reconnectFrame(delayMs, reason));
  }

  sendResyncRequired(conversationIds: string[] = [], full = false): void {
    this.send(resyncRequiredFrame(conversationIds, full));
  }

  close(code = 1000, reason = ''): void {
    if (this.closed) return;
    this.sink.closeSocket(code, reason);
    this.markClosed(code);
  }

  drop(): void {
    if (this.closed) return;
    this.sink.dropSocket();
    this.markClosed(1006);
  }

  simulateSilence(silent = true): void {
    this.silent = silent;
  }

  expectFrame(matcher: FrameMatcher, options?: ExpectFrameOptions): Promise<ClientFrameJson> {
    return this.core.expectFrameOn(this.id, matcher, options);
  }

  /** Called by the transport once the socket is really gone. */
  markClosed(code: number): void {
    if (this.closed) return;
    this.closed = true;
    this.closeCode = code;
  }

  /** Feed one inbound WebSocket text frame through the protocol. */
  handleText(text: string): void {
    let parsed: ClientFrameJson;
    try {
      // Decoding through the generated schema is the point: it asserts that the client puts
      // valid proto3 JSON on the wire, unknown fields included.
      fromJsonString(ClientFrameSchema, text);
      parsed = JSON.parse(text) as ClientFrameJson;
    } catch (error) {
      this.sendError('ERROR_CODE_INVALID_FRAME', { message: String(error) });
      this.close(1009, 'invalid frame');
      return;
    }
    this.received.push(parsed);
    this.core.recordFrame(this, parsed, text);
    this.route(parsed);
  }

  private route(frame: ClientFrameJson): void {
    const bodyCase = clientFrameCase(frame);
    const rid = this.options.echoHelloRid ? (frame.rid ?? '') : '';

    if (!this.authenticated) {
      if (bodyCase !== 'hello') {
        this.sendError('ERROR_CODE_HELLO_REQUIRED', {
          ...(rid === '' ? {} : { rid }),
          message: 'Hello must be the first frame',
        });
        this.close(1008, 'hello required');
        return;
      }
      this.handleHello(frame, rid);
      return;
    }

    switch (bodyCase) {
      case 'hello':
        this.sendError('ERROR_CODE_ALREADY_AUTHENTICATED', {
          ...(rid === '' ? {} : { rid }),
          message: 'already authenticated',
        });
        break;
      case 'ping':
        if (this.options.autoPong && !this.silent) {
          this.send(pongFrame(frame.ping?.ts ?? '0', { serverTime: timestampJson(Date.now()) }));
        }
        break;
      case 'bye':
        this.close(1000, 'bye');
        return;
      case undefined:
        this.sendError('ERROR_CODE_INVALID_FRAME', { message: 'empty oneof body' });
        break;
    }
    this.runScripts(frame);
  }

  private handleHello(frame: ClientFrameJson, rid: string): void {
    const hello = frame.hello ?? {};
    if ((hello.protoVersion ?? 0) !== this.options.protoVersion) {
      this.sendError('ERROR_CODE_PROTOCOL_VERSION_UNSUPPORTED', {
        ...(rid === '' ? {} : { rid }),
        message: `this server speaks protocol version ${this.options.protoVersion.toString()}`,
      });
      this.close(1002, 'unsupported protocol version');
      return;
    }
    const verdict = this.options.authenticate(hello);
    const result: MockAuthResult = typeof verdict === 'boolean' ? { ok: verdict } : verdict;
    if (!result.ok) {
      this.sendError(result.code ?? 'ERROR_CODE_UNAUTHENTICATED', {
        ...(rid === '' ? {} : { rid }),
        message: result.message ?? 'invalid token',
      });
      this.close(result.closeCode ?? 1008, 'unauthenticated');
      return;
    }
    this.authenticated = true;
    if (this.options.autoWelcome) {
      this.send(
        welcomeFrame({
          heartbeatIntervalMs: this.options.heartbeatIntervalMs,
          ...this.options.welcome,
        }),
      );
    }
    this.runScripts(frame);
  }

  private runScripts(frame: ClientFrameJson): void {
    let replied = false;
    for (const script of this.core.scripts) {
      if (!frameMatches(script.matcher, frame)) continue;
      const reply = script.responder(frame, this);
      if (reply === undefined) continue;
      this.send(withRid(reply, frame.rid ?? ''));
      replied = true;
    }
    const rid = frame.rid ?? '';
    if (!replied && this.options.autoAck && rid !== '' && clientFrameCase(frame) !== 'hello') {
      this.ack(rid);
    }
  }
}

/** Fill in a missing `rid` on a scripted `Ack`/`Error` reply. */
function withRid(frame: ServerFrameJson, rid: string): ServerFrameJson {
  if (rid === '') return frame;
  if (frame.ack !== undefined && frame.ack.rid === undefined) {
    return { ...frame, ack: { ...frame.ack, rid } };
  }
  if (frame.error !== undefined && frame.error.rid === undefined) {
    return { ...frame, error: { ...frame.error, rid } };
  }
  return frame;
}

/**
 * Transport-independent mock Talky server.
 *
 * `startMockServer` (real `ws` sockets) and `startMemoryServer` (in-process sockets) are thin
 * shells over this class, so both speak exactly the same protocol and honour exactly the same
 * scripting API.
 */
export class MockServerCore implements MockServerControl {
  readonly connections: MockSession[] = [];
  readonly received: ReceivedFrame[] = [];
  readonly scripts: Script[] = [];

  private readonly options: ResolvedOptions;
  private readonly frameWaiters = new Set<FrameWaiter>();
  private readonly connectionWaiters = new Set<{
    index: number;
    resolve: (connection: MockConnection) => void;
    reject: (error: Error) => void;
    timer: ReturnType<typeof setTimeout>;
  }>();
  private nextId = 0;

  /** Replaced by the transport so `close()` also tears the socket layer down. */
  closeHook: () => Promise<void> = () => Promise.resolve();

  constructor(options: MockServerOptions = {}) {
    this.options = resolveOptions(options);
  }

  /** Called by a transport when a socket is accepted. */
  register(sink: ConnectionSink): MockSession {
    this.nextId += 1;
    const session = new MockSession(this.nextId, sink, this, this.options);
    this.connections.push(session);
    for (const waiter of [...this.connectionWaiters]) {
      if (this.connections.length > waiter.index) {
        this.connectionWaiters.delete(waiter);
        clearTimeout(waiter.timer);
        const found = this.connections[waiter.index];
        if (found !== undefined) waiter.resolve(found);
      }
    }
    return session;
  }

  /** Called by {@link MockSession} for every decoded inbound frame. */
  recordFrame(session: MockSession, frame: ClientFrameJson, raw: string): void {
    this.received.push({ connectionId: session.id, frame, raw });
    for (const waiter of [...this.frameWaiters]) {
      if (waiter.connectionId !== null && waiter.connectionId !== session.id) continue;
      if (!frameMatches(waiter.matcher, frame)) continue;
      this.frameWaiters.delete(waiter);
      clearTimeout(waiter.timer);
      waiter.resolve(frame);
    }
  }

  latest(): MockSession {
    const last = this.connections.at(-1);
    if (last === undefined) throw new Error('mock server: no connection has been accepted yet');
    return last;
  }

  waitForConnection(index = 0, timeoutMs = DEFAULT_EXPECT_TIMEOUT_MS): Promise<MockConnection> {
    const existing = this.connections[index];
    if (existing !== undefined) return Promise.resolve(existing);
    return new Promise<MockConnection>((resolve, reject) => {
      const waiter = {
        index,
        resolve,
        reject,
        timer: setTimeout(() => {
          this.connectionWaiters.delete(waiter);
          reject(
            new Error(
              `mock server: no connection #${index.toString()} within ${timeoutMs.toString()} ms ` +
                `(have ${this.connections.length.toString()})`,
            ),
          );
        }, timeoutMs),
      };
      this.connectionWaiters.add(waiter);
    });
  }

  send(frame: ServerFrameJson): void {
    this.latest().send(frame);
  }

  expectFrame(matcher: FrameMatcher, options?: ExpectFrameOptions): Promise<ClientFrameJson> {
    return this.expectFrameOn(null, matcher, options);
  }

  /** {@link expectFrame}, optionally restricted to one connection. */
  expectFrameOn(
    connectionId: number | null,
    matcher: FrameMatcher,
    options: ExpectFrameOptions = {},
  ): Promise<ClientFrameJson> {
    const includeBuffered = options.includeBuffered ?? true;
    if (includeBuffered) {
      for (const entry of this.received) {
        if (connectionId !== null && entry.connectionId !== connectionId) continue;
        if (frameMatches(matcher, entry.frame)) return Promise.resolve(entry.frame);
      }
    }
    const timeoutMs = options.timeoutMs ?? DEFAULT_EXPECT_TIMEOUT_MS;
    return new Promise<ClientFrameJson>((resolve, reject) => {
      const waiter: FrameWaiter = {
        matcher,
        connectionId,
        resolve,
        reject,
        timer: setTimeout(() => {
          this.frameWaiters.delete(waiter);
          reject(
            new Error(
              `mock server: no client frame matching ${describeMatcher(matcher)} within ` +
                `${timeoutMs.toString()} ms`,
            ),
          );
        }, timeoutMs),
      };
      this.frameWaiters.add(waiter);
    });
  }

  dropConnection(): void {
    this.latest().drop();
  }

  sendReconnect(delayMs: number, reason = 'draining'): void {
    this.latest().sendReconnect(delayMs, reason);
  }

  simulateSilence(silent = true): void {
    this.latest().simulateSilence(silent);
  }

  onRequest(
    matcher: FrameMatcher,
    responder: (frame: ClientFrameJson, connection: MockConnection) => ServerFrameJson | undefined,
  ): () => void {
    const script: Script = { matcher, responder };
    this.scripts.push(script);
    return () => {
      const index = this.scripts.indexOf(script);
      if (index >= 0) this.scripts.splice(index, 1);
    };
  }

  async close(): Promise<void> {
    for (const waiter of this.frameWaiters) clearTimeout(waiter.timer);
    this.frameWaiters.clear();
    for (const waiter of this.connectionWaiters) clearTimeout(waiter.timer);
    this.connectionWaiters.clear();
    for (const session of this.connections) session.close(1001, 'server shutting down');
    await this.closeHook();
  }
}
