import { describe, expect, it } from 'vitest';
import { createFakeClock, DEFAULT_CLOCK_START_MS } from '../src/index.js';

describe('createFakeClock', () => {
  it('starts at the documented epoch and only moves when advanced', () => {
    const clock = createFakeClock();
    expect(clock.now()).toBe(DEFAULT_CLOCK_START_MS);
    clock.advance(1_000);
    expect(clock.now()).toBe(DEFAULT_CLOCK_START_MS + 1_000);
  });

  it('fires timers in deadline order, then in scheduling order', () => {
    const clock = createFakeClock();
    const order: string[] = [];
    clock.setTimeout(() => order.push('b'), 100);
    clock.setTimeout(() => order.push('a'), 50);
    clock.setTimeout(() => order.push('c'), 100);
    clock.advance(100);
    expect(order).toEqual(['a', 'b', 'c']);
    expect(clock.pending).toBe(0);
  });

  it('sets `now` to each timer deadline as it fires', () => {
    const clock = createFakeClock(0);
    const seen: number[] = [];
    clock.setTimeout(() => seen.push(clock.now()), 30);
    clock.setTimeout(() => seen.push(clock.now()), 70);
    clock.advance(100);
    expect(seen).toEqual([30, 70]);
    expect(clock.now()).toBe(100);
  });

  it('runs timers armed from inside a timer', () => {
    const clock = createFakeClock(0);
    const seen: number[] = [];
    clock.setTimeout(() => {
      seen.push(clock.now());
      clock.setTimeout(() => seen.push(clock.now()), 10);
    }, 10);
    clock.advance(25);
    expect(seen).toEqual([10, 20]);
  });

  it('cancels timers and reports what is armed', () => {
    const clock = createFakeClock(0);
    const handle = clock.setTimeout(() => {
      expect.unreachable('cancelled timer fired');
    }, 10);
    clock.setTimeout(() => undefined, 40);
    clock.clearTimeout(handle);
    expect(clock.armedDeadlines()).toEqual([40]);
    clock.advance(100);
    expect(clock.pending).toBe(0);
  });

  it('settles the event loop between timers in advanceAsync', async () => {
    const clock = createFakeClock(0);
    const order: string[] = [];
    clock.setTimeout(() => {
      order.push('timer');
      void Promise.resolve().then(() => order.push('microtask'));
    }, 10);
    clock.setTimeout(() => order.push('later timer'), 20);
    await clock.advanceAsync(30);
    expect(order).toEqual(['timer', 'microtask', 'later timer']);
  });

  it('drains everything with runAll and clears with clearAll', () => {
    const clock = createFakeClock(0);
    let fired = 0;
    clock.setTimeout(() => {
      fired += 1;
    }, 10);
    clock.setTimeout(() => {
      fired += 1;
    }, 10_000);
    clock.runAll();
    expect(fired).toBe(2);

    clock.setTimeout(() => {
      expect.unreachable('cleared timer fired');
    }, 5);
    clock.clearAll();
    clock.advance(100);
    expect(clock.pending).toBe(0);
  });

  it('detects a timer storm instead of hanging', () => {
    const clock = createFakeClock(0);
    const rearm = (): void => {
      clock.setTimeout(rearm, 0);
    };
    rearm();
    expect(() => {
      clock.advance(1);
    }).toThrow(/timer storm/);
  });
});
