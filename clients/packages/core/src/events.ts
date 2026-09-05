/** Shape of an event map: event name → listener argument tuple. */
export type EventMap = Record<string, unknown[]>;

/** Listener for a single event of an {@link EventMap}. */
export type Listener<A extends unknown[]> = (...args: A) => void;

type ErasedListener = (...args: never) => void;

/** Read-only half of an {@link Emitter}: what {@link WsClient} exposes to apps. */
export interface EventSubscriber<E extends EventMap> {
  /**
   * Subscribe to `event`.
   *
   * @returns an unsubscribe function; calling it twice is a no-op.
   */
  on<K extends keyof E & string>(event: K, listener: Listener<E[K]>): () => void;
  /** Subscribe until the first delivery, then unsubscribe. */
  once<K extends keyof E & string>(event: K, listener: Listener<E[K]>): () => void;
  /** Remove a previously registered listener. */
  off<K extends keyof E & string>(event: K, listener: Listener<E[K]>): void;
  /** Number of listeners currently registered for `event`. Used by leak tests. */
  listenerCount(event: keyof E & string): number;
}

/** Typed event emitter that never lets a listener throw into its caller. */
export interface Emitter<E extends EventMap> extends EventSubscriber<E> {
  /**
   * Invoke every listener of `event`.
   *
   * Listeners are called on a snapshot of the listener set, so subscribing or unsubscribing
   * from inside a listener is safe.
   *
   * @returns the errors thrown by listeners, in call order. Never throws.
   */
  emit<K extends keyof E & string>(event: K, ...args: E[K]): readonly unknown[];
  /** Drop every listener of every event. */
  removeAllListeners(): void;
}

const NOTHING_THROWN: readonly unknown[] = Object.freeze([]);

/** Create a typed {@link Emitter}. */
export function createEmitter<E extends EventMap>(): Emitter<E> {
  const registry = new Map<string, Set<ErasedListener>>();

  function off<K extends keyof E & string>(event: K, listener: Listener<E[K]>): void {
    const set = registry.get(event);
    if (set === undefined) return;
    set.delete(listener);
    if (set.size === 0) registry.delete(event);
  }

  function on<K extends keyof E & string>(event: K, listener: Listener<E[K]>): () => void {
    let set = registry.get(event);
    if (set === undefined) {
      set = new Set<ErasedListener>();
      registry.set(event, set);
    }
    set.add(listener);
    return () => {
      off(event, listener);
    };
  }

  return {
    on,
    off,
    once<K extends keyof E & string>(event: K, listener: Listener<E[K]>): () => void {
      const wrapped = ((...args: E[K]) => {
        off(event, wrapped);
        listener(...args);
      }) as Listener<E[K]>;
      return on(event, wrapped);
    },
    listenerCount(event) {
      return registry.get(event)?.size ?? 0;
    },
    emit<K extends keyof E & string>(event: K, ...args: E[K]): readonly unknown[] {
      const set = registry.get(event);
      if (set === undefined || set.size === 0) return NOTHING_THROWN;
      let thrown: unknown[] | undefined;
      for (const erased of [...set]) {
        const listener = erased as unknown as Listener<E[K]>;
        try {
          listener(...args);
        } catch (error) {
          thrown ??= [];
          thrown.push(error);
        }
      }
      return thrown ?? NOTHING_THROWN;
    },
    removeAllListeners() {
      registry.clear();
    },
  };
}
