import type { ClientFrameJson } from '@yanki/protocol/v1';
import { clientFrameCase } from './frames.js';
import type { FrameMatcher } from './types.js';

/**
 * Placeholders that mean "this field must be present, its exact value does not matter".
 * `docs/protocol.md` uses `"…"`; the ASCII spelling is accepted as a convenience.
 */
const WILDCARDS: ReadonlySet<string> = new Set(['…', '...']);

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * `int64` fields travel as strings in proto3 JSON, but a hand-written fixture may spell a
 * small number without quotes. Compare scalars through their string form when the two sides
 * disagree only about that.
 */
function scalarEquals(expected: unknown, actual: unknown): boolean {
  if (expected === actual) return true;
  const expectedIsNumeric = typeof expected === 'string' || typeof expected === 'number';
  const actualIsNumeric = typeof actual === 'string' || typeof actual === 'number';
  if (expectedIsNumeric && actualIsNumeric) return String(expected) === String(actual);
  return false;
}

/**
 * Structural (subset) comparison used by the golden-fixture runner.
 *
 * Rules, straight out of `docs/protocol.md`:
 *
 * - `"…"` matches any present, non-null value (ids and timestamps).
 * - Objects are matched as a **subset**: every key the fixture spells out must match, extra
 *   keys on the actual frame are ignored. That mirrors the protocol's "unknown fields are
 *   ignored" rule and keeps fixtures from breaking when the client starts sending a new
 *   optional field.
 * - Arrays must have the same length and match element-wise.
 * - Scalars compare by value, with `int64`-as-string normalisation.
 *
 * @returns `null` when it matches, otherwise a human-readable explanation.
 */
export function matchStructural(expected: unknown, actual: unknown, path = '$'): string | null {
  if (typeof expected === 'string' && WILDCARDS.has(expected)) {
    return actual === undefined || actual === null
      ? `${path}: expected any value, got ${String(actual)}`
      : null;
  }
  if (Array.isArray(expected)) {
    if (!Array.isArray(actual)) return `${path}: expected an array, got ${typeName(actual)}`;
    if (expected.length !== actual.length) {
      return `${path}: expected ${expected.length.toString()} items, got ${actual.length.toString()}`;
    }
    for (let i = 0; i < expected.length; i += 1) {
      const mismatch = matchStructural(expected[i], actual[i], `${path}[${i.toString()}]`);
      if (mismatch !== null) return mismatch;
    }
    return null;
  }
  if (isPlainObject(expected)) {
    if (!isPlainObject(actual)) return `${path}: expected an object, got ${typeName(actual)}`;
    for (const [key, value] of Object.entries(expected)) {
      const mismatch = matchStructural(value, actual[key], `${path}.${key}`);
      if (mismatch !== null) return mismatch;
    }
    return null;
  }
  if (scalarEquals(expected, actual)) return null;
  return `${path}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`;
}

function typeName(value: unknown): string {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  return typeof value;
}

/** Apply a {@link FrameMatcher} to a decoded client frame. */
export function frameMatches(matcher: FrameMatcher, frame: ClientFrameJson): boolean {
  if (typeof matcher === 'function') return matcher(frame);
  if (typeof matcher === 'string') return clientFrameCase(frame) === matcher;
  return matchStructural(matcher, frame) === null;
}

/** Describe a matcher for assertion messages. */
export function describeMatcher(matcher: FrameMatcher): string {
  if (typeof matcher === 'function') return 'predicate';
  if (typeof matcher === 'string') return `body case '${matcher}'`;
  return JSON.stringify(matcher);
}
