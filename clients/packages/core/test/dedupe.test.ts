import { describe, expect, it } from 'vitest';
import { createFrameDeduper } from '../src/index.js';

describe('createFrameDeduper', () => {
  it('accepts an id once and rejects the replay', () => {
    const deduper = createFrameDeduper(8);
    expect(deduper.acceptId('a')).toBe(true);
    expect(deduper.acceptId('a')).toBe(false);
    expect(deduper.acceptId('b')).toBe(true);
    expect(deduper.size).toBe(2);
  });

  it('never records an empty id', () => {
    const deduper = createFrameDeduper(4);
    expect(deduper.acceptId('')).toBe(true);
    expect(deduper.acceptId('')).toBe(true);
    expect(deduper.size).toBe(0);
  });

  it('de-duplicates (conversationId, seq) pairs independently of ids', () => {
    const deduper = createFrameDeduper(16);
    expect(deduper.acceptSeq('c1', 7n)).toBe(true);
    expect(deduper.acceptSeq('c1', 7n)).toBe(false);
    expect(deduper.acceptSeq('c2', 7n)).toBe(true);
    expect(deduper.acceptSeq('c1', 8n)).toBe(true);
    // int64 arrives as a string over proto3 JSON; it must key identically.
    expect(deduper.acceptSeq('c1', '8')).toBe(false);
  });

  it('rejects a frame when either key repeats', () => {
    const deduper = createFrameDeduper(16);
    expect(deduper.accept({ id: 'e1', conversationId: 'c1', seq: 1n })).toBe(true);
    expect(deduper.accept({ id: 'e2', conversationId: 'c1', seq: 1n })).toBe(false);
    expect(deduper.accept({ id: 'e1', conversationId: 'c1', seq: 2n })).toBe(false);
    expect(deduper.accept({ id: 'e3', conversationId: 'c1', seq: 3n })).toBe(true);
  });

  it('evicts least-recently-seen keys at capacity', () => {
    const deduper = createFrameDeduper(3);
    deduper.acceptId('a');
    deduper.acceptId('b');
    deduper.acceptId('c');
    // Touch 'a' so 'b' becomes the oldest.
    expect(deduper.acceptId('a')).toBe(false);
    deduper.acceptId('d');
    expect(deduper.size).toBe(3);
    expect(deduper.acceptId('b')).toBe(true);
    expect(deduper.acceptId('a')).toBe(false);
  });

  it('clears and reports its capacity', () => {
    const deduper = createFrameDeduper(5);
    deduper.acceptId('a');
    expect(deduper.capacity).toBe(5);
    deduper.clear();
    expect(deduper.size).toBe(0);
    expect(deduper.acceptId('a')).toBe(true);
  });

  it('rejects a nonsensical capacity', () => {
    expect(() => createFrameDeduper(0)).toThrow(RangeError);
    expect(() => createFrameDeduper(-1)).toThrow(RangeError);
    expect(() => createFrameDeduper(1.5)).toThrow(RangeError);
  });
});
