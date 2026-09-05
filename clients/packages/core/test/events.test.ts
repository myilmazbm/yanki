import { describe, expect, it, vi } from 'vitest';
import { createEmitter } from '../src/index.js';

interface TestEvents extends Record<string, unknown[]> {
  ping: [value: number];
  done: [];
}

describe('createEmitter', () => {
  it('delivers arguments and unsubscribes through the returned function', () => {
    const emitter = createEmitter<TestEvents>();
    const seen: number[] = [];
    const off = emitter.on('ping', (value) => seen.push(value));
    emitter.emit('ping', 1);
    emitter.emit('ping', 2);
    off();
    emitter.emit('ping', 3);
    expect(seen).toEqual([1, 2]);
    expect(emitter.listenerCount('ping')).toBe(0);
  });

  it('supports once and off', () => {
    const emitter = createEmitter<TestEvents>();
    const once = vi.fn();
    const persistent = vi.fn();
    emitter.once('done', once);
    emitter.on('done', persistent);
    emitter.emit('done');
    emitter.emit('done');
    expect(once).toHaveBeenCalledTimes(1);
    expect(persistent).toHaveBeenCalledTimes(2);
    emitter.off('done', persistent);
    emitter.emit('done');
    expect(persistent).toHaveBeenCalledTimes(2);
  });

  it('collects listener errors instead of throwing, and still runs the rest', () => {
    const emitter = createEmitter<TestEvents>();
    const after = vi.fn();
    emitter.on('ping', () => {
      throw new Error('boom');
    });
    emitter.on('ping', after);
    const thrown = emitter.emit('ping', 1);
    expect(thrown).toHaveLength(1);
    expect((thrown[0] as Error).message).toBe('boom');
    expect(after).toHaveBeenCalledTimes(1);
  });

  it('is safe to subscribe or unsubscribe from inside a listener', () => {
    const emitter = createEmitter<TestEvents>();
    const late = vi.fn();
    emitter.on('ping', () => {
      emitter.on('ping', late);
    });
    emitter.emit('ping', 1);
    expect(late).not.toHaveBeenCalled();
    emitter.emit('ping', 2);
    expect(late).toHaveBeenCalledTimes(1);
  });

  it('drops everything on removeAllListeners', () => {
    const emitter = createEmitter<TestEvents>();
    emitter.on('ping', () => undefined);
    emitter.on('done', () => undefined);
    emitter.removeAllListeners();
    expect(emitter.listenerCount('ping')).toBe(0);
    expect(emitter.listenerCount('done')).toBe(0);
  });
});
