/**
 * Frame de-duplication.
 *
 * `docs/protocol.md` → "Yeniden bağlanma ve resume": after a resume the server may replay a
 * frame the client already applied. The client drops the second copy of the same `id`, and
 * the second copy of the same `(conversationId, seq)` pair.
 */

/** A frame-shaped value the deduper can key on. */
export interface DedupeKeyable {
  /** Server event id (UUIDv7). */
  id?: string | undefined;
  /** Conversation the frame belongs to, when it is conversation-scoped. */
  conversationId?: string | undefined;
  /** Per-conversation sequence. `int64` arrives as `bigint` or, from JSON, as `string`. */
  seq?: bigint | number | string | undefined;
}

/** Bounded LRU de-duplicator. */
export interface FrameDeduper {
  /**
   * Record `id`.
   *
   * @returns `true` when the id is new (the frame should be processed), `false` when it is a
   * duplicate that must be dropped. An empty id is always accepted and never recorded.
   */
  acceptId(id: string): boolean;
  /**
   * Record a `(conversationId, seq)` pair.
   *
   * @returns `true` when the pair is new, `false` when it is a duplicate.
   */
  acceptSeq(conversationId: string, seq: bigint | number | string): boolean;
  /**
   * Apply both keys to a frame-shaped value: the frame is accepted only when *every* key it
   * carries is new. Both keys are recorded even when the first one already rejected the
   * frame, so a partially-replayed stream still converges.
   */
  accept(frame: DedupeKeyable): boolean;
  /** Number of keys currently retained. */
  readonly size: number;
  /** Maximum number of keys retained before the oldest are evicted. */
  readonly capacity: number;
  /** Forget everything. */
  clear(): void;
}

/**
 * Key namespaces. The separator is a character that cannot occur in a UUID or in a decimal
 * `seq`, so an id key can never collide with a sequence key.
 */
const SEP = '/';

function idKey(id: string): string {
  return `i${SEP}${id}`;
}

function seqKey(conversationId: string, seq: bigint | number | string): string {
  return `s${SEP}${conversationId}${SEP}${seq.toString()}`;
}

/**
 * Create a bounded LRU de-duplicator.
 *
 * @param capacity maximum number of keys to retain (default 1024). Keys are evicted in
 * least-recently-seen order, which is the right policy here: a replay always concerns the
 * most recent frames.
 */
export function createFrameDeduper(capacity = 1024): FrameDeduper {
  if (!Number.isInteger(capacity) || capacity <= 0) {
    throw new RangeError(
      `createFrameDeduper: capacity must be a positive integer, got ${String(capacity)}`,
    );
  }
  // Insertion order in a Map is the LRU order; re-seeing a key moves it back to the end.
  const seen = new Map<string, true>();

  function accept(key: string): boolean {
    if (seen.has(key)) {
      seen.delete(key);
      seen.set(key, true);
      return false;
    }
    seen.set(key, true);
    if (seen.size > capacity) {
      const oldest = seen.keys().next();
      if (oldest.done !== true) seen.delete(oldest.value);
    }
    return true;
  }

  return {
    acceptId(id) {
      if (id === '') return true;
      return accept(idKey(id));
    },
    acceptSeq(conversationId, seq) {
      return accept(seqKey(conversationId, seq));
    },
    accept(frame) {
      let fresh = true;
      const id = frame.id;
      if (id !== undefined && id !== '') {
        fresh = accept(idKey(id)) && fresh;
      }
      const conversationId = frame.conversationId;
      const seq = frame.seq;
      if (conversationId !== undefined && conversationId !== '' && seq !== undefined) {
        fresh = accept(seqKey(conversationId, seq)) && fresh;
      }
      return fresh;
    },
    get size() {
      return seen.size;
    },
    capacity,
    clear() {
      seen.clear();
    },
  };
}
