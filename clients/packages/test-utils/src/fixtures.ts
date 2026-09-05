import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import type { FakeClock } from './clock.js';
import { flush } from './clock.js';
import { matchStructural } from './match.js';
import type { MockServerControl } from './types.js';

/** One entry of a golden fixture. */
export interface WsFixtureEntry {
  /** `c2s` = client → server, `s2c` = server → client. */
  dir: 'c2s' | 's2c';
  /** The frame, as proto3 JSON. `"…"` means "present, value irrelevant". */
  json?: Record<string, unknown>;
  /**
   * Accepted extension to the documented format: an `s2c` entry may close the connection
   * instead of sending a frame, which is how a reconnect flow is expressed.
   */
  close?: { code?: number; reason?: string };
}

/** A golden fixture from `api/fixtures/ws/*.json`. */
export interface WsFixture {
  /** File name without the extension. */
  name: string;
  /** Absolute path of the source file. */
  file: string;
  description?: string;
  frames: WsFixtureEntry[];
}

/**
 * Locate `api/fixtures/ws` by walking up from `startDir`.
 *
 * @returns the absolute directory, or `null` when the repository has no fixtures yet.
 */
export function findFixturesDir(startDir: string = process.cwd()): string | null {
  let dir = resolve(startDir);
  for (;;) {
    const candidate = join(dir, 'api', 'fixtures', 'ws');
    if (existsSync(candidate)) return candidate;
    const parent = dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

function parseFixture(file: string): WsFixture {
  const parsed: unknown = JSON.parse(readFileSync(file, 'utf8'));
  if (typeof parsed !== 'object' || parsed === null) {
    throw new Error(`${file}: fixture root must be an object`);
  }
  const record = parsed as Record<string, unknown>;
  const frames = record.frames;
  if (!Array.isArray(frames)) throw new Error(`${file}: "frames" must be an array`);
  const entries: WsFixtureEntry[] = frames.map((raw, index) => {
    if (typeof raw !== 'object' || raw === null) {
      throw new Error(`${file}: frames[${index.toString()}] must be an object`);
    }
    const entry = raw as Record<string, unknown>;
    const direction = entry.dir;
    if (direction !== 'c2s' && direction !== 's2c') {
      throw new Error(`${file}: frames[${index.toString()}].dir must be "c2s" or "s2c"`);
    }
    const result: WsFixtureEntry = { dir: direction };
    if (entry.json !== undefined) result.json = entry.json as Record<string, unknown>;
    if (entry.close !== undefined) {
      result.close = entry.close as { code?: number; reason?: string };
    }
    if (result.json === undefined && result.close === undefined) {
      throw new Error(`${file}: frames[${index.toString()}] needs either "json" or "close"`);
    }
    return result;
  });
  const fixture: WsFixture = {
    name: basename(file).replace(/\.json$/, ''),
    file,
    frames: entries,
  };
  const description = record.description;
  if (typeof description === 'string') fixture.description = description;
  return fixture;
}

/**
 * Load every golden fixture, sorted by name.
 *
 * @param dir directory to read; defaults to the discovered `api/fixtures/ws`.
 * @returns an empty array when the directory does not exist or holds no `.json` file. The
 * caller is expected to report that explicitly rather than silently passing.
 */
export function loadWsFixtures(dir: string | null = findFixturesDir()): WsFixture[] {
  if (dir === null || !existsSync(dir)) return [];
  return readdirSync(dir)
    .filter((name) => name.endsWith('.json'))
    .sort((a, b) => a.localeCompare(b))
    .map((name) => parseFixture(join(dir, name)));
}

/** What {@link replayFixture} needs in order to drive a client. */
export interface ReplayContext {
  /** A mock server the client under test is already wired to. */
  server: MockServerControl;
  /** The virtual clock the client under test uses. */
  clock: FakeClock;
  /** Starts the client connecting. Called once, before the first entry. */
  start: () => void | Promise<void>;
  /** Virtual time to burn while waiting for an expected `c2s` frame. Default 120 000 ms. */
  maxAdvanceMs?: number;
  /** Virtual step size while waiting. Default 250 ms. */
  stepMs?: number;
}

/** Outcome of a successful replay. */
export interface ReplayResult {
  fixture: WsFixture;
  /** Number of `c2s` frames verified. */
  matched: number;
}

/**
 * Replay one golden fixture against a live client.
 *
 * `s2c` entries are played by the mock server; `c2s` entries are matched, in order, against
 * what the client actually put on the wire — structurally, so `"…"` fields only have to be
 * present and extra fields the client sends are tolerated.
 *
 * Because the client's clock is virtual, waiting for a `c2s` frame that is only produced by a
 * timer (a heartbeat `Ping`, a `Hello` after reconnect backoff) simply advances that clock
 * until the frame shows up.
 */
export async function replayFixture(
  fixture: WsFixture,
  context: ReplayContext,
): Promise<ReplayResult> {
  const maxAdvanceMs = context.maxAdvanceMs ?? 120_000;
  const stepMs = context.stepMs ?? 250;
  const { server, clock } = context;

  await context.start();
  await flush();

  let cursor = 0;
  for (const [index, entry] of fixture.frames.entries()) {
    const where = `${fixture.name} frames[${index.toString()}]`;
    if (entry.dir === 's2c') {
      if (entry.close !== undefined) {
        server.latest().close(entry.close.code ?? 1000, entry.close.reason ?? '');
      } else if (entry.json !== undefined) {
        server.send(entry.json);
      }
      await flush();
      continue;
    }

    let advanced = 0;
    while (server.received.length <= cursor) {
      if (advanced >= maxAdvanceMs) {
        throw new Error(
          `${where}: the client never sent the expected frame ` +
            `${JSON.stringify(entry.json)} (waited ${maxAdvanceMs.toString()} ms of virtual time)`,
        );
      }
      await clock.advanceAsync(stepMs);
      advanced += stepMs;
    }

    const actual = server.received[cursor];
    if (actual === undefined) throw new Error(`${where}: internal cursor error`);
    const mismatch = matchStructural(entry.json, actual.frame);
    if (mismatch !== null) {
      throw new Error(
        `${where}: ${mismatch}\n  expected: ${JSON.stringify(entry.json)}\n  actual:   ${JSON.stringify(actual.frame)}`,
      );
    }
    cursor += 1;
  }

  return { fixture, matched: cursor };
}
