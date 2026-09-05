import { describe, expect, it } from 'vitest';
import { createUuidV7, isUuidV7, UUID_V7_PATTERN, uuidv7 } from '../src/index.js';
import { seededRandom } from './helpers.js';

describe('uuidv7', () => {
  it('produces canonical version 7, variant 1 UUIDs', () => {
    for (let i = 0; i < 200; i += 1) {
      const id = uuidv7();
      expect(id).toMatch(UUID_V7_PATTERN);
      expect(isUuidV7(id)).toBe(true);
      expect(id).toHaveLength(36);
    }
  });

  it('encodes the timestamp in the leading 48 bits', () => {
    const at = 1_725_540_000_000;
    const next = createUuidV7({ now: () => at, random: seededRandom(7) });
    const id = next();
    const timestampHex = id.slice(0, 8) + id.slice(9, 13);
    expect(Number.parseInt(timestampHex, 16)).toBe(at);
  });

  it('is strictly ascending inside one millisecond', () => {
    const next = createUuidV7({ now: () => 1_725_540_000_000, random: seededRandom(1) });
    const ids = Array.from({ length: 5_000 }, () => next());
    for (let i = 1; i < ids.length; i += 1) {
      expect((ids[i] ?? '') > (ids[i - 1] ?? '')).toBe(true);
    }
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('stays ascending when the wall clock jumps backwards', () => {
    let now = 1_725_540_000_000;
    const next = createUuidV7({ now: () => now, random: seededRandom(2) });
    const first = next();
    now -= 60_000;
    const second = next();
    expect(second > first).toBe(true);
  });

  it('sorts by time across milliseconds', () => {
    let now = 1_725_540_000_000;
    const next = createUuidV7({ now: () => now, random: seededRandom(3) });
    const ids: string[] = [];
    for (let i = 0; i < 100; i += 1) {
      ids.push(next());
      now += 1;
    }
    expect([...ids].sort((a, b) => a.localeCompare(b))).toEqual(ids);
  });

  it('is reproducible for a given clock and random source', () => {
    const build = (): string[] => {
      const next = createUuidV7({ now: () => 1_725_540_000_000, random: seededRandom(42) });
      return [next(), next(), next()];
    };
    expect(build()).toEqual(build());
  });
});
