/**
 * Exponential backoff with proportional jitter, per `docs/protocol.md`:
 * 0.5 s initial, ×2 per attempt, 30 s ceiling, ±20 %.
 */
export interface BackoffOptions {
  /** Delay before the first retry, in milliseconds. */
  initialMs: number;
  /** Ceiling applied before jitter. */
  maxMs: number;
  /** Growth factor per attempt. */
  factor: number;
  /** Relative jitter in `[0, 1]`: the delay lands in `base × (1 ± jitter)`. */
  jitter: number;
}

/** Protocol defaults. */
export const DEFAULT_BACKOFF: BackoffOptions = Object.freeze({
  initialMs: 500,
  maxMs: 30_000,
  factor: 2,
  jitter: 0.2,
});

/**
 * Delay before retry number `attempt` (0-based: `attempt = 0` is the first retry after a
 * drop).
 *
 * @param random uniform source in `[0, 1)`; injected so the sequence is reproducible in tests.
 */
export function backoffDelayMs(
  attempt: number,
  options: BackoffOptions,
  random: () => number,
): number {
  const growth = Math.pow(options.factor, Math.max(0, attempt));
  const base = Math.min(options.maxMs, options.initialMs * growth);
  const spread = base * options.jitter;
  const delay = base - spread + random() * spread * 2;
  return Math.max(0, Math.round(delay));
}
