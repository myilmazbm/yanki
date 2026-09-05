import { describe, expect, it } from 'vitest';
import { clientFrameCase, frameMatches, matchStructural, testUuidV7 } from '../src/index.js';

describe('matchStructural', () => {
  it('treats "…" as present-and-not-null', () => {
    expect(matchStructural({ id: '…' }, { id: 'abc' })).toBeNull();
    expect(matchStructural({ id: '…' }, { id: 0 })).toBeNull();
    expect(matchStructural({ id: '…' }, {})).toContain('expected any value');
    expect(matchStructural({ id: '...' }, { id: 'abc' })).toBeNull();
  });

  it('matches objects as a subset so extra client fields are tolerated', () => {
    const actual = { rid: 'r1', hello: { protoVersion: 1, token: 't', deviceId: 'd' } };
    expect(matchStructural({ hello: { protoVersion: 1 } }, actual)).toBeNull();
    expect(matchStructural({ hello: { protoVersion: 2 } }, actual)).toContain('expected 2, got 1');
  });

  it('compares int64 written as a string against a numeric value', () => {
    expect(matchStructural({ ts: '1725540000000' }, { ts: 1_725_540_000_000 })).toBeNull();
    expect(matchStructural({ seq: 7 }, { seq: '7' })).toBeNull();
    expect(matchStructural({ seq: '7' }, { seq: '8' })).toContain('expected "7"');
  });

  it('requires arrays to match element-wise and by length', () => {
    expect(matchStructural({ ids: ['a', 'b'] }, { ids: ['a', 'b'] })).toBeNull();
    expect(matchStructural({ ids: ['a'] }, { ids: ['a', 'b'] })).toContain('expected 1 items');
    expect(matchStructural({ ids: ['a', '…'] }, { ids: ['a', 'z'] })).toBeNull();
  });

  it('reports the failing path', () => {
    const mismatch = matchStructural(
      { hello: { client: { platform: 'web' } } },
      { hello: { client: { platform: 'ios' } } },
    );
    expect(mismatch).toContain('$.hello.client.platform');
  });
});

describe('frameMatches', () => {
  it('selects by oneof case, subset object or predicate', () => {
    const ping = { ping: { ts: '1' } };
    expect(frameMatches('ping', ping)).toBe(true);
    expect(frameMatches('hello', ping)).toBe(false);
    expect(frameMatches({ ping: { ts: '1' } }, ping)).toBe(true);
    expect(frameMatches((frame) => frame.ping?.ts === '1', ping)).toBe(true);
  });

  it('reports the oneof case of a client frame', () => {
    expect(clientFrameCase({ hello: {} })).toBe('hello');
    expect(clientFrameCase({ bye: {} })).toBe('bye');
    expect(clientFrameCase({ rid: 'r' })).toBeUndefined();
  });
});

describe('testUuidV7', () => {
  it('produces monotonically increasing version 7 ids', () => {
    const ids = Array.from({ length: 500 }, () => testUuidV7(1_725_540_000_000));
    expect(ids[0]).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    for (let i = 1; i < ids.length; i += 1) expect((ids[i] ?? '') > (ids[i - 1] ?? '')).toBe(true);
  });
});
