import type {
  ConversationStore,
  Draft,
  DraftStore,
  KvStore,
  MessageStore,
  OutboxEntry,
  OutboxStore,
  SeqRange,
  Storage,
  StoredConversation,
  StoredMessage,
} from './Storage.js';

/**
 * In-memory {@link Storage}. Zero dependencies, no platform APIs — it is the reference
 * implementation the real adapters are tested against, and the store the unit tests use.
 *
 * Values are copied on the way in and out, so callers cannot mutate rows behind its back.
 */
export class MemoryStorage implements Storage {
  private readonly kvData = new Map<string, string>();
  private readonly conversationData = new Map<string, StoredConversation>();
  private readonly messageData = new Map<string, StoredMessage>();
  private readonly outboxData = new Map<string, OutboxEntry>();
  private readonly draftData = new Map<string, Draft>();

  readonly kv: KvStore = {
    get: (key) => Promise.resolve(this.kvData.get(key)),
    set: (key, value) => {
      this.kvData.set(key, value);
      return Promise.resolve();
    },
    delete: (key) => {
      this.kvData.delete(key);
      return Promise.resolve();
    },
    keys: (prefix) =>
      Promise.resolve(
        [...this.kvData.keys()].filter((key) => prefix === undefined || key.startsWith(prefix)),
      ),
  };

  readonly conversations: ConversationStore = {
    get: (id) => Promise.resolve(copy(this.conversationData.get(id))),
    list: () =>
      Promise.resolve(
        [...this.conversationData.values()]
          .map((row) => ({ ...row }))
          .sort((a, b) => b.lastActivityAt - a.lastActivityAt),
      ),
    upsert: (conversation) => {
      this.conversationData.set(conversation.id, { ...conversation });
      return Promise.resolve();
    },
    delete: (id) => {
      this.conversationData.delete(id);
      return Promise.resolve();
    },
    sinceCursor: (limit = 50) => {
      const cursor: Record<string, bigint> = {};
      const rows = [...this.conversationData.values()]
        .filter((row) => !row.archived)
        .sort((a, b) => b.lastActivityAt - a.lastActivityAt)
        .slice(0, limit);
      for (const row of rows) cursor[row.id] = row.lastSeq;
      return Promise.resolve(cursor);
    },
  };

  readonly messages: MessageStore = {
    get: (id) => Promise.resolve(copy(this.messageData.get(id))),
    list: (conversationId, range) => Promise.resolve(this.listMessages(conversationId, range)),
    upsert: (message) => {
      this.messageData.set(message.id, { ...message });
      return Promise.resolve();
    },
    delete: (id) => {
      this.messageData.delete(id);
      return Promise.resolve();
    },
    maxSeq: (conversationId) => {
      let max: bigint | undefined;
      for (const row of this.messageData.values()) {
        if (row.conversationId !== conversationId) continue;
        if (max === undefined || row.seq > max) max = row.seq;
      }
      return Promise.resolve(max);
    },
  };

  readonly outbox: OutboxStore = {
    enqueue: (entry) => {
      this.outboxData.set(entry.clientMsgId, { ...entry });
      return Promise.resolve();
    },
    list: (limit) => {
      const rows = [...this.outboxData.values()]
        .map((row) => ({ ...row }))
        .sort((a, b) => a.createdAt - b.createdAt);
      return Promise.resolve(limit === undefined ? rows : rows.slice(0, limit));
    },
    update: (clientMsgId, patch) => {
      const current = this.outboxData.get(clientMsgId);
      if (current !== undefined) this.outboxData.set(clientMsgId, { ...current, ...patch });
      return Promise.resolve();
    },
    remove: (clientMsgId) => {
      this.outboxData.delete(clientMsgId);
      return Promise.resolve();
    },
  };

  readonly drafts: DraftStore = {
    get: (conversationId) => Promise.resolve(copy(this.draftData.get(conversationId))),
    set: (draft) => {
      this.draftData.set(draft.conversationId, { ...draft });
      return Promise.resolve();
    },
    delete: (conversationId) => {
      this.draftData.delete(conversationId);
      return Promise.resolve();
    },
    list: () => Promise.resolve([...this.draftData.values()].map((row) => ({ ...row }))),
  };

  open(): Promise<void> {
    return Promise.resolve();
  }

  close(): Promise<void> {
    return Promise.resolve();
  }

  clear(): Promise<void> {
    this.kvData.clear();
    this.conversationData.clear();
    this.messageData.clear();
    this.outboxData.clear();
    this.draftData.clear();
    return Promise.resolve();
  }

  private listMessages(conversationId: string, range: SeqRange): StoredMessage[] {
    const rows = [...this.messageData.values()]
      .filter(
        (row) =>
          row.conversationId === conversationId &&
          row.seq >= range.fromSeq &&
          row.seq <= range.toSeq,
      )
      .sort((a, b) => (a.seq < b.seq ? -1 : a.seq > b.seq ? 1 : 0))
      .map((row) => ({ ...row }));
    return range.limit === undefined ? rows : rows.slice(-range.limit);
  }
}

function copy<T extends object>(value: T | undefined): T | undefined {
  return value === undefined ? undefined : { ...value };
}
