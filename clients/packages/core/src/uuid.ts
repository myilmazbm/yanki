/**
 * UUIDv7 generation (RFC 9562 §5.7).
 *
 * Layout, most significant bit first:
 *
 * ```text
 *  0                   1                   2                   3
 *  0..47   unix_ts_ms   48-bit big-endian milliseconds since the Unix epoch
 *  48..51  ver          0b0111
 *  52..63  rand_a       12-bit monotonic counter (RFC 9562 §6.2 "method 1")
 *  64..65  var          0b10
 *  66..127 rand_b       62 random bits
 * ```
 *
 * `rand_a` is used as a per-millisecond counter so that ids minted inside the same
 * millisecond still sort strictly ascending — which is what makes a `rid`/`id` usable as a
 * de-duplication and ordering key.
 */

/** Injection seam so tests can generate deterministic, reproducible ids. */
export interface UuidV7Deps {
  /** Milliseconds since the Unix epoch. */
  now: () => number;
  /** Uniform random number in `[0, 1)`. */
  random: () => number;
}

/** Highest value the 12-bit `rand_a` counter can hold. */
const COUNTER_MAX = 0xfff;

/**
 * Seed range for the first id of a millisecond. Seeding in the lower half keeps ~2048
 * increments of headroom before the counter has to borrow from the next millisecond, while
 * still not being fully predictable.
 */
const COUNTER_SEED_RANGE = 0x800;

function hex(value: number, digits: number): string {
  return value.toString(16).padStart(digits, '0');
}

function format(unixTsMs: number, counter: number, random: () => number): string {
  // 48-bit timestamp split into a 32-bit high half and a 16-bit low half.
  const tsHigh = Math.floor(unixTsMs / 0x10000);
  const tsLow = unixTsMs % 0x10000;
  // 62 random bits: 14 + 24 + 24.
  const randBHigh = Math.floor(random() * 0x4000);
  const randBMid = Math.floor(random() * 0x1000000);
  const randBLow = Math.floor(random() * 0x1000000);

  return [
    hex(tsHigh, 8),
    hex(tsLow, 4),
    `7${hex(counter & COUNTER_MAX, 3)}`,
    hex(0x8000 | randBHigh, 4),
    hex(randBMid, 6) + hex(randBLow, 6),
  ].join('-');
}

/**
 * Create a UUIDv7 generator with injected time and randomness.
 *
 * The returned function is monotonic: two ids produced in the same millisecond (or while the
 * wall clock runs backwards) still compare strictly ascending as strings.
 */
export function createUuidV7(deps: UuidV7Deps): () => string {
  let lastMs = -1;
  let counter = 0;

  return function uuidV7(): string {
    const observed = Math.max(0, Math.trunc(deps.now()));
    if (observed > lastMs) {
      lastMs = observed;
      counter = Math.floor(deps.random() * COUNTER_SEED_RANGE);
    } else {
      counter += 1;
      if (counter > COUNTER_MAX) {
        // Sub-millisecond overflow: borrow from the next millisecond so ordering holds.
        lastMs += 1;
        counter = 0;
      }
    }
    return format(lastMs, counter, deps.random);
  };
}

const defaultGenerator = createUuidV7({
  now: () => Date.now(),
  // `Math.random` is deliberate: `rid` / `id` are correlation and de-duplication keys, never
  // secrets. Apps that want CSPRNG-backed ids inject `random` through `WsClientOptions`.
  random: () => Math.random(),
});

/** Generate a UUIDv7 using the ambient clock and `Math.random`. */
export function uuidv7(): string {
  return defaultGenerator();
}

/** Matches the canonical textual form of a version 7, variant 1 UUID. */
export const UUID_V7_PATTERN =
  /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/** Whether `value` is a canonical UUIDv7 string. */
export function isUuidV7(value: string): boolean {
  return UUID_V7_PATTERN.test(value);
}
