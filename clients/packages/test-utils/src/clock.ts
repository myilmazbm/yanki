import type { TimerHostLike } from './types.js';

/** Default virtual epoch: 2024-09-05T12:00:00Z, matching the fixture examples. */
export const DEFAULT_CLOCK_START_MS = 1_725_540_000_000;

/** Guard against a timer that endlessly re-schedules itself at the same instant. */
const MAX_TIMERS_PER_ADVANCE = 100_000;

/**
 * Virtual clock that satisfies `@yanki/core`'s `TimerHost`.
 *
 * This — not Vitest's global fake timers — is how the realtime tests control time: the client
 * only ever reads the clock through the injected `TimerHost`, so real network I/O (the `ws`
 * mock server) keeps working while heartbeat windows, pong deadlines, request timeouts and
 * hours of reconnect backoff pass instantly.
 */
export interface FakeClock extends TimerHostLike {
  /** Current virtual time in epoch milliseconds. */
  readonly time: number;
  /** Number of timers currently armed. `0` after a clean shutdown. */
  readonly pending: number;
  /** Advance virtual time by `ms`, firing every timer that comes due, in order. */
  advance(ms: number): void;
  /**
   * Like {@link FakeClock.advance}, but yields to the event loop after every timer so that
   * promise continuations and socket I/O triggered by a timer complete before the next one.
   */
  advanceAsync(ms: number): Promise<void>;
  /** Fire every armed timer, advancing virtual time to each deadline. */
  runAll(): void;
  /** Deadlines of the armed timers, ascending. Useful for asserting on scheduling. */
  armedDeadlines(): number[];
  /** Cancel every armed timer without firing it. */
  clearAll(): void;
}

interface ScheduledTimer {
  at: number;
  seq: number;
  fn: () => void;
}

/** Yield until Node has drained microtasks and the current I/O phase. */
export function flush(): Promise<void> {
  return new Promise<void>((resolve) => {
    setImmediate(resolve);
  });
}

/** Create a {@link FakeClock}. */
export function createFakeClock(startMs: number = DEFAULT_CLOCK_START_MS): FakeClock {
  let time = startMs;
  let seq = 0;
  const timers = new Map<number, ScheduledTimer>();

  function earliestDue(limit: number): { id: number; timer: ScheduledTimer } | null {
    let best: { id: number; timer: ScheduledTimer } | null = null;
    for (const [id, timer] of timers) {
      if (timer.at > limit) continue;
      if (
        best === null ||
        timer.at < best.timer.at ||
        (timer.at === best.timer.at && timer.seq < best.timer.seq)
      ) {
        best = { id, timer };
      }
    }
    return best;
  }

  return {
    setTimeout(handler, timeoutMs) {
      seq += 1;
      const id = seq;
      timers.set(id, { at: time + Math.max(0, timeoutMs), seq: id, fn: handler });
      return id;
    },
    clearTimeout(handle) {
      if (typeof handle === 'number') timers.delete(handle);
    },
    now() {
      return time;
    },
    get time() {
      return time;
    },
    get pending() {
      return timers.size;
    },
    advance(ms) {
      const target = time + Math.max(0, ms);
      let fired = 0;
      for (;;) {
        const due = earliestDue(target);
        if (due === null) break;
        timers.delete(due.id);
        time = Math.max(time, due.timer.at);
        due.timer.fn();
        fired += 1;
        if (fired > MAX_TIMERS_PER_ADVANCE) {
          throw new Error('FakeClock.advance: timer storm — a timer keeps re-arming itself');
        }
      }
      time = target;
    },
    async advanceAsync(ms) {
      const target = time + Math.max(0, ms);
      let fired = 0;
      for (;;) {
        const due = earliestDue(target);
        if (due === null) break;
        timers.delete(due.id);
        time = Math.max(time, due.timer.at);
        due.timer.fn();
        // Sequential by design: each timer's async fallout must settle before the next fires.
        await flush();
        fired += 1;
        if (fired > MAX_TIMERS_PER_ADVANCE) {
          throw new Error('FakeClock.advanceAsync: timer storm — a timer keeps re-arming itself');
        }
      }
      time = target;
      await flush();
    },
    runAll() {
      let guard = 0;
      while (timers.size > 0) {
        let furthest = time;
        for (const timer of timers.values()) furthest = Math.max(furthest, timer.at);
        this.advance(furthest - time);
        guard += 1;
        if (guard > 1000) throw new Error('FakeClock.runAll: timers never drained');
      }
    },
    armedDeadlines() {
      return [...timers.values()].map((timer) => timer.at).sort((a, b) => a - b);
    },
    clearAll() {
      timers.clear();
    },
  };
}
