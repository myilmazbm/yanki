/**
 * `@talky/test-utils` — everything needed to test a Talky client without a server.
 *
 * - {@link startMemoryServer} — in-process transport + scriptable protocol (default choice).
 * - {@link startMockServer} — the same protocol over real `ws` sockets.
 * - {@link createFakeClock} — virtual `TimerHost`, so hours of backoff pass instantly.
 * - {@link loadWsFixtures} / {@link replayFixture} — the `api/fixtures/ws/*.json` golden runner.
 *
 * This package deliberately does not depend on `@talky/core`; see `src/types.ts`.
 */

export { createFakeClock, DEFAULT_CLOCK_START_MS, flush } from './clock.js';
export type { FakeClock } from './clock.js';

export {
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

export { describeMatcher, frameMatches, matchStructural } from './match.js';

export { MockServerCore, MockSession } from './server-core.js';
export type { ConnectionSink } from './server-core.js';

export { MemorySocket, startMemoryServer } from './memory-server.js';
export type { MemoryServer } from './memory-server.js';

export { startMockServer } from './mock-server.js';
export type { MockServer, MockServerStartOptions } from './mock-server.js';

export {
  expectedClientStateAfterClose,
  findFixturesDir,
  frameBodyKey,
  helloProfile,
  loadWsFixtures,
  replayFixture,
} from './fixtures.js';
export type {
  ReplayContext,
  ReplayResult,
  WsFixture,
  WsFixtureClose,
  WsFixtureDirection,
  WsFixtureEntry,
  WsFixtureHelloProfile,
} from './fixtures.js';

export type {
  CloseEventLike,
  ExpectFrameOptions,
  FrameMatcher,
  MessageEventLike,
  MockAuthResult,
  MockConnection,
  MockServerControl,
  MockServerOptions,
  ReceivedFrame,
  TimerHostLike,
  WebSocketFactory,
  WebSocketLike,
} from './types.js';
