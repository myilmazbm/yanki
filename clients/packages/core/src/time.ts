/**
 * Opaque handle returned by {@link TimerHost.setTimeout}.
 *
 * Browsers hand back a `number`, Node a `Timeout` object and the test clock an internal
 * record, so callers must treat it as opaque and only ever pass it back to
 * {@link TimerHost.clearTimeout}.
 */
export type TimerHandle = unknown;

/**
 * The only clock `@yanki/core` is allowed to see.
 *
 * Everything time-related in the client (heartbeat cadence, pong deadline, request
 * timeouts, reconnect backoff) goes through this seam, which is what makes the whole state
 * machine testable with a virtual clock and no real waiting.
 */
export interface TimerHost {
  /** Schedule `handler` to run once, at least `timeoutMs` from now. */
  setTimeout(handler: () => void, timeoutMs: number): TimerHandle;
  /** Cancel a timer previously created by {@link TimerHost.setTimeout}. */
  clearTimeout(handle: TimerHandle): void;
  /** Milliseconds since the Unix epoch. Used for RTT samples and the heartbeat window. */
  now(): number;
}

/**
 * `setTimeout` / `clearTimeout` are not declared by the `ES2022` lib and `@yanki/core`
 * deliberately compiles without the DOM lib or Node types, so the two globals it genuinely
 * needs are declared here instead of dragging in a whole platform lib.
 */
interface TimerGlobals {
  setTimeout: (handler: () => void, timeoutMs: number) => TimerHandle;
  clearTimeout: (handle: TimerHandle) => void;
}

const timerGlobals = globalThis as unknown as TimerGlobals;

/** {@link TimerHost} backed by the ambient `setTimeout` / `clearTimeout` / `Date.now`. */
export const systemTimerHost: TimerHost = {
  setTimeout(handler, timeoutMs) {
    return timerGlobals.setTimeout(handler, timeoutMs);
  },
  clearTimeout(handle) {
    timerGlobals.clearTimeout(handle);
  },
  now() {
    return Date.now();
  },
};
