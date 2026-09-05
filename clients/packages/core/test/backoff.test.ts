import { describe, expect, it } from 'vitest';
import { backoffDelayMs, DEFAULT_BACKOFF } from '../src/index.js';

describe('backoffDelayMs', () => {
  it('follows 0.5 s → ×2 → 30 s with no jitter', () => {
    const zeroJitter = { ...DEFAULT_BACKOFF, jitter: 0 };
    const mid = (): number => 0.5;
    const series = Array.from({ length: 9 }, (_, attempt) =>
      backoffDelayMs(attempt, zeroJitter, mid),
    );
    expect(series).toEqual([500, 1000, 2000, 4000, 8000, 16_000, 30_000, 30_000, 30_000]);
  });

  it('keeps jitter inside ±20 % of the base delay', () => {
    for (let attempt = 0; attempt < 12; attempt += 1) {
      const base = Math.min(
        DEFAULT_BACKOFF.maxMs,
        DEFAULT_BACKOFF.initialMs * DEFAULT_BACKOFF.factor ** attempt,
      );
      for (const sample of [0, 0.25, 0.5, 0.75, 0.999999]) {
        const delay = backoffDelayMs(attempt, DEFAULT_BACKOFF, () => sample);
        expect(delay).toBeGreaterThanOrEqual(Math.round(base * 0.8) - 1);
        expect(delay).toBeLessThanOrEqual(Math.round(base * 1.2) + 1);
      }
    }
  });

  it('maps random()=0 to the lower bound and random()→1 to the upper bound', () => {
    expect(backoffDelayMs(0, DEFAULT_BACKOFF, () => 0)).toBe(400);
    expect(backoffDelayMs(0, DEFAULT_BACKOFF, () => 0.999999)).toBe(600);
    expect(backoffDelayMs(3, DEFAULT_BACKOFF, () => 0)).toBe(3200);
    expect(backoffDelayMs(3, DEFAULT_BACKOFF, () => 0.999999)).toBe(4800);
  });

  it('never returns a negative delay and never overflows past the ceiling', () => {
    expect(backoffDelayMs(-5, DEFAULT_BACKOFF, () => 0)).toBe(400);
    expect(backoffDelayMs(1000, DEFAULT_BACKOFF, () => 0)).toBe(24_000);
    expect(backoffDelayMs(1000, DEFAULT_BACKOFF, () => 1)).toBe(36_000);
  });
});
