import { createFakeClock, startMemoryServer } from '@talky/test-utils';
import type {
  MemorySocket,
  TimerHostLike,
  WebSocketLike as MockWebSocketLike,
} from '@talky/test-utils';
import { describe, expect, it } from 'vitest';
import type { TimerHost, WebSocketFactory, WebSocketLike } from '../src/index.js';
import { WS_CLOSED, WS_CLOSING, WS_CONNECTING, WS_OPEN } from '../src/index.js';

/**
 * `@talky/test-utils` mirrors `WebSocketLike` and `TimerHost` structurally instead of importing
 * them (a package cycle would break Turborepo's task graph). These assignments are the pin that
 * makes any drift between the two definitions a compile error.
 */
const clockIsATimerHost: TimerHost = createFakeClock();
const timerHostIsACompatibleClock: TimerHostLike = clockIsATimerHost;
const mockSocketIsAWebSocket: (socket: MemorySocket) => WebSocketLike = (socket) => socket;
const mirroredSocketIsAWebSocket: (socket: MockWebSocketLike) => WebSocketLike = (socket) => socket;

describe('cross-package contracts', () => {
  it('accepts the fake clock as a TimerHost', () => {
    expect(typeof clockIsATimerHost.now()).toBe('number');
    expect(typeof timerHostIsACompatibleClock.now()).toBe('number');
  });

  it('accepts the memory server factory as a WebSocketFactory', () => {
    const server = startMemoryServer();
    const factory: WebSocketFactory = server.webSocketFactory;
    const socket = factory('ws://mock.talky.local/ws');
    expect(mockSocketIsAWebSocket(socket as MemorySocket).readyState).toBe(WS_CONNECTING);
    expect(mirroredSocketIsAWebSocket(socket).readyState).toBe(WS_CONNECTING);
  });

  it('exposes the RFC 6455 readyState constants', () => {
    expect([WS_CONNECTING, WS_OPEN, WS_CLOSING, WS_CLOSED]).toEqual([0, 1, 2, 3]);
  });
});
