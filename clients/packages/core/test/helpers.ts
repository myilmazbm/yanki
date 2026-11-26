import type { ResyncRequired, ServerFrame, Welcome } from '@yanki/protocol/v1';
import type { FakeClock, MemoryServer, MockServerOptions } from '@yanki/test-utils';
import { createFakeClock, flush, startMemoryServer } from '@yanki/test-utils';
import { createWsClient } from '../src/index.js';
import type {
  ReconnectScheduled,
  YankiWsError,
  WsClient,
  WsClientOptions,
  WsClientState,
} from '../src/index.js';

/**
 * Deterministic PRNG (mulberry32).
 *
 * Injected as `WsClientOptions.random`, it makes jitter, `rid`s and the `Reconnect` delay
 * window fully reproducible — a failing chaos run can be replayed byte for byte from its seed.
 */
export function seededRandom(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = Math.imul(state ^ (state >>> 15), 1 | state);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4_294_967_296;
  };
}

/** Everything a realtime test needs, wired together. */
export interface Harness {
  client: WsClient;
  server: MemoryServer;
  clock: FakeClock;
  /** Errors delivered through the `error` event. */
  errors: YankiWsError[];
  /** States entered, in order, excluding the initial `idle`. */
  states: WsClientState[];
  reconnects: ReconnectScheduled[];
  welcomes: Welcome[];
  frames: ServerFrame[];
  resyncs: ResyncRequired[];
  rtts: number[];
  /** Number of `open` events. */
  opens: () => number;
  /** Let queued microtasks and socket hand-offs run. */
  settle: (rounds?: number) => Promise<void>;
  /** `connect()` and wait until the handshake completes. */
  open: () => Promise<void>;
  /** Advance the virtual clock, settling between timers. */
  advance: (ms: number) => Promise<void>;
  /** Wait until `predicate` holds, advancing the clock in small steps. */
  waitUntil: (
    predicate: () => boolean,
    options?: { maxMs?: number; stepMs?: number },
  ) => Promise<void>;
  dispose: () => Promise<void>;
}

/** Options accepted by {@link createHarness}. */
export interface HarnessOptions {
  client?: Partial<WsClientOptions>;
  server?: MockServerOptions;
  seed?: number;
  /** Auto-connect and wait for `Welcome` before returning. */
  autoOpen?: boolean;
}

/** Wire a `WsClient` to an in-process mock server driven by a virtual clock. */
export function createHarness(options: HarnessOptions = {}): Harness {
  const clock = createFakeClock();
  const server = startMemoryServer(options.server);
  const random = options.client?.random ?? seededRandom(options.seed ?? 0x5eed);

  const client = createWsClient({
    url: 'ws://mock.yanki.local/ws',
    getToken: () => Promise.resolve('dev:u1'),
    deviceId: '11111111-2222-4333-8444-555555555555',
    client: { platform: 'web', appVersion: '0.1.0', os: 'Windows 11', locale: 'tr-TR' },
    webSocketFactory: server.webSocketFactory,
    timers: clock,
    random,
    ...options.client,
  });

  const errors: YankiWsError[] = [];
  const states: WsClientState[] = [];
  const reconnects: ReconnectScheduled[] = [];
  const welcomes: Welcome[] = [];
  const frames: ServerFrame[] = [];
  const resyncs: ResyncRequired[] = [];
  const rtts: number[] = [];
  let openCount = 0;

  client.on('error', (error) => errors.push(error));
  client.on('stateChange', (state) => states.push(state));
  client.on('reconnectScheduled', (info) => reconnects.push(info));
  client.on('welcome', (welcome) => welcomes.push(welcome));
  client.on('frame', (frame) => frames.push(frame));
  client.on('resyncRequired', (resync) => resyncs.push(resync));
  client.on('rtt', (rtt) => rtts.push(rtt));
  client.on('open', () => {
    openCount += 1;
  });

  const settle = async (rounds = 4): Promise<void> => {
    for (let i = 0; i < rounds; i += 1) await flush();
  };

  const advance = async (ms: number): Promise<void> => {
    await clock.advanceAsync(ms);
    await settle();
  };

  const waitUntil = async (
    predicate: () => boolean,
    waitOptions: { maxMs?: number; stepMs?: number } = {},
  ): Promise<void> => {
    const maxMs = waitOptions.maxMs ?? 300_000;
    const stepMs = waitOptions.stepMs ?? 100;
    await settle();
    let spent = 0;
    while (!predicate()) {
      if (spent >= maxMs) {
        throw new Error(
          `waitUntil: condition still false after ${maxMs.toString()} ms of virtual time ` +
            `(state '${client.state}')`,
        );
      }
      await clock.advanceAsync(stepMs);
      await settle(2);
      spent += stepMs;
    }
  };

  const harness: Harness = {
    client,
    server,
    clock,
    errors,
    states,
    reconnects,
    welcomes,
    frames,
    resyncs,
    rtts,
    opens: () => openCount,
    settle,
    open: async () => {
      client.connect();
      await waitUntil(() => client.state === 'open', { maxMs: 60_000 });
    },
    advance,
    waitUntil,
    dispose: async () => {
      client.close();
      await settle();
      clock.clearAll();
      await server.close();
    },
  };

  return harness;
}
