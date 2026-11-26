import { create } from '@bufbuild/protobuf';
import { HelloSchema, PingSchema } from '@yanki/protocol/v1';
import { describe, expect, it } from 'vitest';
import { clientFrame, decodeServerFrame, encodeClientFrame } from '../src/index.js';

describe('wire codec', () => {
  it('encodes a Hello as proto3 JSON with the oneof name as the frame type', () => {
    const frame = clientFrame(
      {
        case: 'hello',
        value: create(HelloSchema, {
          protoVersion: 1,
          token: 'dev:u1',
          deviceId: 'device-1',
          since: { c1: 42n },
        }),
      },
      '0190f0a1-0000-7000-8000-000000000001',
    );
    const json: unknown = JSON.parse(encodeClientFrame(frame));
    expect(json).toEqual({
      rid: '0190f0a1-0000-7000-8000-000000000001',
      hello: {
        protoVersion: 1,
        token: 'dev:u1',
        deviceId: 'device-1',
        // int64 map values are strings in proto3 JSON.
        since: { c1: '42' },
      },
    });
  });

  it('omits default values and the rid of fire-and-forget frames', () => {
    const frame = clientFrame({
      case: 'ping',
      value: create(PingSchema, { ts: 1_725_540_000_000n }),
    });
    const json: unknown = JSON.parse(encodeClientFrame(frame));
    expect(json).toEqual({ ping: { ts: '1725540000000' } });
  });

  it('decodes a Welcome and exposes the oneof case', () => {
    const frame = decodeServerFrame(
      JSON.stringify({
        id: '0190f0a1-0000-7000-8000-000000000002',
        welcome: { sessionId: 's1', userId: 'u1', heartbeatIntervalMs: 25000 },
      }),
    );
    expect(frame.id).toBe('0190f0a1-0000-7000-8000-000000000002');
    expect(frame.body.case).toBe('welcome');
    if (frame.body.case !== 'welcome') throw new Error('unreachable');
    expect(frame.body.value.sessionId).toBe('s1');
    expect(frame.body.value.heartbeatIntervalMs).toBe(25_000);
  });

  it('ignores unknown fields for forward compatibility', () => {
    const frame = decodeServerFrame(
      JSON.stringify({
        id: 'e1',
        somethingFromTheFuture: { nested: true },
        welcome: { sessionId: 's1', capabilities: ['calls'] },
      }),
    );
    expect(frame.body.case).toBe('welcome');
  });

  it('reports an unknown oneof body as an empty body', () => {
    const frame = decodeServerFrame(JSON.stringify({ id: 'e1', totallyNewFrame: {} }));
    expect(frame.body.case).toBeUndefined();
  });

  it('throws on malformed JSON and on a wrongly typed field', () => {
    expect(() => decodeServerFrame('{ not json')).toThrow();
    expect(() => decodeServerFrame(JSON.stringify({ id: 42 }))).toThrow();
  });

  it('round-trips int64 as a string', () => {
    const frame = decodeServerFrame(JSON.stringify({ id: 'e1', pong: { ts: '9007199254740993' } }));
    if (frame.body.case !== 'pong') throw new Error('unreachable');
    expect(frame.body.value.ts).toBe(9_007_199_254_740_993n);
  });
});
