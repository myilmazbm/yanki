import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import type { FakeClock } from './clock.js';
import { flush } from './clock.js';
import { fromJson } from '@bufbuild/protobuf';
import { ServerFrameSchema } from '@talky/protocol/v1';
import type { ServerFrameJson } from '@talky/protocol/v1';
import { testUuidV7, timestampJson } from './frames.js';
import { matchStructural } from './match.js';
import type { MockServerControl } from './types.js';

/**
 * Direction of a fixture entry.
 *
 * - `c2s` — client → server frame, asserted against what the client put on the wire.
 * - `s2c` — server → client frame, played by the mock server.
 * - `server-action` — an instruction to the *server-side* harness (e.g. `drain`). No client
 *   frame can trigger it, so the client runner skips it; the `s2c` frames and the top-level
 *   `close` that follow describe everything the client observes.
 */
export type WsFixtureDirection = 'c2s' | 's2c' | 'server-action';

/** One entry of a golden fixture. */
export interface WsFixtureEntry {
  dir: WsFixtureDirection;
  /** The frame, as proto3 JSON. `"…"` means "present, value irrelevant". */
  json?: Record<string, unknown>;
  /** `server-action` only: the action name (`drain`, …). */
  action?: string;
  /**
   * An `s2c` entry may close the connection instead of sending a frame, which is how a
   * mid-sequence drop is expressed. The documented top-level `close` covers the common
   * "server ends the connection after the last frame" case.
   */
  close?: WsFixtureClose;
}

/** How the server ends the connection. */
export interface WsFixtureClose {
  code?: number;
  reason?: string;
}

/** A golden fixture from `api/fixtures/ws/*.json`. */
export interface WsFixture {
  /** File name without the extension. */
  name: string;
  /** Absolute path of the source file. */
  file: string;
  description?: string;
  frames: WsFixtureEntry[];
  /** Top-level `close`: the server closes with this code after the last frame. */
  close?: WsFixtureClose;
  /**
   * `false` when the sequence contains client-side protocol violations a conforming client
   * cannot produce (a `Ping` before `Hello`, a second `Hello`, an unsupported version). Such
   * fixtures are exercised by the server-side contract tests only. Default `true`.
   */
  clientReplay: boolean;
}

/** The identity a fixture's first `Hello` carries; used to configure the client under test. */
export interface WsFixtureHelloProfile {
  token?: string;
  deviceId?: string;
  client?: { platform: string; appVersion: string; os: string; locale: string };
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

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function parseClose(value: unknown, where: string): WsFixtureClose {
  if (!isPlainObject(value)) throw new Error(`${where}: "close" must be an object`);
  const close: WsFixtureClose = {};
  if (typeof value.code === 'number') close.code = value.code;
  if (typeof value.reason === 'string') close.reason = value.reason;
  return close;
}

function parseEntry(raw: unknown, where: string): WsFixtureEntry {
  if (!isPlainObject(raw)) throw new Error(`${where} must be an object`);
  const direction = raw.dir;
  if (direction === 'server-action') {
    if (typeof raw.action !== 'string' || raw.action === '') {
      throw new Error(`${where}: a server-action entry needs a non-empty "action"`);
    }
    return { dir: 'server-action', action: raw.action };
  }
  if (direction !== 'c2s' && direction !== 's2c') {
    throw new Error(`${where}.dir must be "c2s", "s2c" or "server-action"`);
  }
  const result: WsFixtureEntry = { dir: direction };
  if (isPlainObject(raw.json)) result.json = raw.json;
  if (raw.close !== undefined) result.close = parseClose(raw.close, where);
  if (result.json === undefined && result.close === undefined) {
    throw new Error(`${where} needs either "json" or "close"`);
  }
  return result;
}

function parseFixture(file: string): WsFixture {
  const parsed: unknown = JSON.parse(readFileSync(file, 'utf8'));
  if (!isPlainObject(parsed)) throw new Error(`${file}: fixture root must be an object`);
  const frames = parsed.frames;
  if (!Array.isArray(frames)) throw new Error(`${file}: "frames" must be an array`);

  const fixture: WsFixture = {
    name: basename(file).replace(/\.json$/, ''),
    file,
    frames: frames.map((raw, index) => parseEntry(raw, `${file}: frames[${index.toString()}]`)),
    clientReplay: parsed.clientReplay !== false,
  };
  if (typeof parsed.description === 'string') fixture.description = parsed.description;
  if (parsed.close !== undefined) fixture.close = parseClose(parsed.close, file);
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

/** The `oneof body` case of a frame written as JSON (`hello`, `ping`, `bye`, …). */
export function frameBodyKey(frame: Record<string, unknown> | undefined): string | undefined {
  if (frame === undefined) return undefined;
  return Object.keys(frame).find((key) => key !== 'rid' && key !== 'id');
}

/**
 * Extract the identity the fixture's first `Hello` carries so the client under test can be
 * configured to send exactly that (token, device, client descriptor).
 */
export function helloProfile(fixture: WsFixture): WsFixtureHelloProfile {
  const profile: WsFixtureHelloProfile = {};
  const first = fixture.frames.find(
    (entry) => entry.dir === 'c2s' && frameBodyKey(entry.json) === 'hello',
  );
  const hello = first?.json?.hello;
  if (!isPlainObject(hello)) return profile;
  if (typeof hello.token === 'string') profile.token = hello.token;
  if (typeof hello.deviceId === 'string') profile.deviceId = hello.deviceId;
  const client = hello.client;
  if (
    isPlainObject(client) &&
    typeof client.platform === 'string' &&
    typeof client.appVersion === 'string' &&
    typeof client.os === 'string' &&
    typeof client.locale === 'string'
  ) {
    profile.client = {
      platform: client.platform,
      appVersion: client.appVersion,
      os: client.os,
      locale: client.locale,
    };
  }
  return profile;
}

/** Close codes after which a conforming client must not retry (see docs/protocol.md). */
const PERMANENT_CLOSE_CODES: ReadonlySet<number> = new Set([1002, 1003, 1008]);

/**
 * The client state a conforming client must be in once the fixture's top-level `close` has
 * been observed: `closed` after its own `Bye` or a permanent code, `reconnecting` otherwise.
 */
export function expectedClientStateAfterClose(fixture: WsFixture): 'closed' | 'reconnecting' {
  const lastClientEntry = [...fixture.frames].reverse().find((entry) => entry.dir === 'c2s');
  if (frameBodyKey(lastClientEntry?.json) === 'bye') return 'closed';
  const code = fixture.close?.code ?? 1000;
  return PERMANENT_CLOSE_CODES.has(code) ? 'closed' : 'reconnecting';
}

const WILDCARDS: ReadonlySet<string> = new Set(['…', '...']);

/**
 * Fields the *client* chooses, so a fixture's concrete value (needed by the server-side tests,
 * which send `c2s` frames verbatim) is never compared against what the client sent:
 * the request id and the `Ping` timestamp.
 */
function stripClientChosen(expected: Record<string, unknown>): Record<string, unknown> {
  const { rid: _rid, ...rest } = expected;
  if (isPlainObject(rest.ping)) {
    const { ts: _ts, ...ping } = rest.ping;
    rest.ping = ping;
  }
  return rest;
}

/**
 * `"…"` in an `s2c` frame means "the server puts a real value here"; the server-side tests
 * only check presence. When the *client* runner plays such a frame it has to send something
 * the client can parse, so placeholders are filled by field name: timestamps
 * (`serverTime`, `*Time`, `*At`) as RFC 3339, ids (`id`, `*Id`) as UUIDv7, anything else as
 * a plain marker string.
 */
function materializePlaceholders(value: unknown, key: string, nowMs: number): unknown {
  if (typeof value === 'string' && WILDCARDS.has(value)) {
    if (key === 'serverTime' || key.endsWith('Time') || key.endsWith('At'))
      return timestampJson(nowMs);
    if (key === 'id' || key.endsWith('Id')) return testUuidV7(nowMs);
    return 'placeholder';
  }
  if (Array.isArray(value)) return value.map((item) => materializePlaceholders(item, key, nowMs));
  if (isPlainObject(value)) {
    const out: Record<string, unknown> = {};
    for (const [childKey, child] of Object.entries(value)) {
      out[childKey] = materializePlaceholders(child, childKey, nowMs);
    }
    return out;
  }
  return value;
}

interface ServerFrameContext {
  /** fixture-rid → the rid the client actually sent, recorded while matching `c2s` frames. */
  ridMap: ReadonlyMap<string, string>;
  /** `ts` of the last `Ping` the client sent; `Pong.ts` must echo it verbatim. */
  lastPingTs: string | undefined;
  nowMs: number;
}

/**
 * Turn a fixture `s2c` entry into the frame the mock server sends to the client:
 * placeholders materialized, `rid`s mapped to what the client actually sent (`Ack`, `Error`),
 * `Pong.ts` echoing the client's `Ping.ts`. The result is validated against
 * `ServerFrameSchema` so a fixture the client could not parse fails loudly here instead of
 * showing up as a mysterious reconnect.
 */
function prepareServerFrame(
  frame: Record<string, unknown>,
  context: ServerFrameContext,
  where: string,
): ServerFrameJson {
  const materialized = materializePlaceholders(frame, '', context.nowMs) as Record<string, unknown>;
  for (const [key, value] of Object.entries(materialized)) {
    if (!isPlainObject(value)) continue;
    if (typeof value.rid === 'string') {
      const actual = context.ridMap.get(value.rid);
      if (actual !== undefined) materialized[key] = { ...value, rid: actual };
    }
    if (key === 'pong' && context.lastPingTs !== undefined) {
      materialized[key] = { ...value, ts: context.lastPingTs };
    }
  }
  try {
    fromJson(ServerFrameSchema, materialized as ServerFrameJson, { ignoreUnknownFields: true });
  } catch (error) {
    throw new Error(
      `${where}: the s2c frame is not a valid ServerFrame even after placeholder materialization: ` +
        `${error instanceof Error ? error.message : String(error)}\n  frame: ${JSON.stringify(materialized)}`,
    );
  }
  return materialized;
}

/** What {@link replayFixture} needs in order to drive a client. */
export interface ReplayContext {
  /** A mock server the client under test is already wired to. */
  server: MockServerControl;
  /** The virtual clock the client under test uses. */
  clock: FakeClock;
  /** Starts the client connecting. Called once, before the first entry. */
  start: () => void | Promise<void>;
  /**
   * Hooks invoked *before* waiting for a `c2s` frame the client only produces on an app
   * action, keyed by frame body (`bye` → `client.close()`). `hello` and `ping` need none.
   */
  triggers?: Readonly<Record<string, () => void | Promise<void>>>;
  /** Current client state; when supplied, the state after the top-level `close` is asserted. */
  getState?: () => string;
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
  /** Client state after the top-level `close` (or after the last entry), when observable. */
  finalState?: string;
}

/**
 * Replay one golden fixture against a live client.
 *
 * `s2c` entries are played by the mock server; `c2s` entries are matched, in order, against
 * what the client actually put on the wire — structurally, so `"…"` fields only have to be
 * present and extra fields the client sends are tolerated. `rid` is never compared on `c2s`
 * frames (the client generates it) but is carried over into later `s2c` replies.
 * `server-action` entries are skipped. A top-level `close` is played after the last entry
 * and, when `getState` is supplied, the resulting client state is asserted.
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
  const ridMap = new Map<string, string>();
  let lastPingTs: string | undefined;

  await context.start();
  await flush();

  let cursor = 0;
  for (const [index, entry] of fixture.frames.entries()) {
    const where = `${fixture.name} frames[${index.toString()}]`;
    if (entry.dir === 'server-action') continue;

    if (entry.dir === 's2c') {
      if (entry.close !== undefined) {
        const connection = server.latest();
        if (!connection.closed)
          connection.close(entry.close.code ?? 1000, entry.close.reason ?? '');
      } else if (entry.json !== undefined) {
        server.send(
          prepareServerFrame(entry.json, { ridMap, lastPingTs, nowMs: clock.now() }, where),
        );
      }
      await flush();
      continue;
    }

    const expected = entry.json ?? {};
    const trigger = context.triggers?.[frameBodyKey(expected) ?? ''];
    if (trigger !== undefined) {
      await trigger();
      await flush();
    }

    let advanced = 0;
    while (server.received.length <= cursor) {
      if (advanced >= maxAdvanceMs) {
        throw new Error(
          `${where}: the client never sent the expected frame ` +
            `${JSON.stringify(expected)} (waited ${maxAdvanceMs.toString()} ms of virtual time)`,
        );
      }
      await clock.advanceAsync(stepMs);
      advanced += stepMs;
    }

    const actual = server.received[cursor];
    if (actual === undefined) throw new Error(`${where}: internal cursor error`);
    const mismatch = matchStructural(stripClientChosen(expected), actual.frame);
    if (mismatch !== null) {
      throw new Error(
        `${where}: ${mismatch}\n  expected: ${JSON.stringify(expected)}\n  actual:   ${JSON.stringify(actual.frame)}`,
      );
    }
    const expectedRid = expected.rid;
    const actualRid = actual.frame.rid;
    if (typeof expectedRid === 'string' && typeof actualRid === 'string' && actualRid !== '') {
      ridMap.set(expectedRid, actualRid);
    }
    const actualPingTs = actual.frame.ping?.ts;
    if (typeof actualPingTs === 'string') lastPingTs = actualPingTs;
    cursor += 1;
  }

  if (fixture.close !== undefined) {
    const connection = server.latest();
    if (!connection.closed)
      connection.close(fixture.close.code ?? 1000, fixture.close.reason ?? '');
    await flush();
    await flush();
  }

  const result: ReplayResult = { fixture, matched: cursor };
  if (context.getState !== undefined) {
    const finalState = context.getState();
    result.finalState = finalState;
    if (fixture.close !== undefined) {
      const expectedState = expectedClientStateAfterClose(fixture);
      if (finalState !== expectedState) {
        throw new Error(
          `${fixture.name}: after the server closed with ${(fixture.close.code ?? 1000).toString()} ` +
            `the client is '${finalState}', expected '${expectedState}'`,
        );
      }
    }
  }
  return result;
}
