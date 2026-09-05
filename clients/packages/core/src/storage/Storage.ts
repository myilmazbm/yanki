/**
 * Local persistence contract.
 *
 * M0 only defines the shape — the sync engine that fills it lands in M2. It is declared now
 * because the platform packages (web IndexedDB, Tauri SQLite, React Native SQLite) are built
 * against it, and because `@talky/core` must never reach for a concrete store itself.
 *
 * All `seq` values are the server's per-conversation `int64` sequence and are modelled as
 * `bigint` so they never lose precision.
 */

/** Simple string key/value area: device id, session hints, feature flags, cursors. */
export interface KvStore {
  get(key: string): Promise<string | undefined>;
  set(key: string, value: string): Promise<void>;
  delete(key: string): Promise<void>;
  /** Keys in arbitrary order, optionally filtered by prefix. */
  keys(prefix?: string): Promise<string[]>;
}

/** Delivery state of a message as far as this device is concerned. */
export type MessageState = 'pending' | 'sent' | 'failed';

/** A conversation as cached locally. */
export interface StoredConversation {
  id: string;
  title: string;
  /** Highest `seq` the client has applied. Feeds `Hello.since`. */
  lastSeq: bigint;
  /** Epoch millis of the last activity, used for list ordering. */
  lastActivityAt: number;
  unreadCount: number;
  archived: boolean;
}

/** A message as cached locally. */
export interface StoredMessage {
  id: string;
  conversationId: string;
  seq: bigint;
  authorId: string;
  /** Epoch millis. */
  sentAt: number;
  body: string;
  /** Idempotency key of the local send that produced it, when this device sent it. */
  clientMsgId?: string;
  state: MessageState;
}

/** Inclusive `seq` window used for history queries. */
export interface SeqRange {
  fromSeq: bigint;
  toSeq: bigint;
  /** Maximum number of rows to return, newest first. */
  limit?: number;
}

/** A send that has not been acknowledged yet. Survives restarts: this is why messages are never lost. */
export interface OutboxEntry {
  /** Idempotency key sent to the server; retries reuse it. */
  clientMsgId: string;
  conversationId: string;
  body: string;
  /** Epoch millis when the user pressed send. */
  createdAt: number;
  attempts: number;
  lastError?: string;
}

/** An unsent composer draft. */
export interface Draft {
  conversationId: string;
  text: string;
  updatedAt: number;
}

/** Conversation list persistence. */
export interface ConversationStore {
  get(id: string): Promise<StoredConversation | undefined>;
  list(): Promise<StoredConversation[]>;
  upsert(conversation: StoredConversation): Promise<void>;
  delete(id: string): Promise<void>;
  /** `conversationId → lastSeq` for the `Hello.since` cursor. */
  sinceCursor(limit?: number): Promise<Record<string, bigint>>;
}

/** Message history persistence. */
export interface MessageStore {
  get(id: string): Promise<StoredMessage | undefined>;
  /** Messages of `conversationId` inside `range`, ascending by `seq`. */
  list(conversationId: string, range: SeqRange): Promise<StoredMessage[]>;
  upsert(message: StoredMessage): Promise<void>;
  delete(id: string): Promise<void>;
  /** Highest applied `seq`, or `undefined` when the conversation is empty locally. */
  maxSeq(conversationId: string): Promise<bigint | undefined>;
}

/** Durable send queue. */
export interface OutboxStore {
  enqueue(entry: OutboxEntry): Promise<void>;
  /** Oldest-first, so sends keep their order. */
  list(limit?: number): Promise<OutboxEntry[]>;
  update(clientMsgId: string, patch: Partial<Omit<OutboxEntry, 'clientMsgId'>>): Promise<void>;
  remove(clientMsgId: string): Promise<void>;
}

/** Composer drafts. */
export interface DraftStore {
  get(conversationId: string): Promise<Draft | undefined>;
  set(draft: Draft): Promise<void>;
  delete(conversationId: string): Promise<void>;
  list(): Promise<Draft[]>;
}

/** Everything a Talky client persists locally. */
export interface Storage {
  readonly kv: KvStore;
  readonly conversations: ConversationStore;
  readonly messages: MessageStore;
  readonly outbox: OutboxStore;
  readonly drafts: DraftStore;
  /** Open/migrate the underlying store. Safe to call more than once. */
  open(): Promise<void>;
  close(): Promise<void>;
  /** Drop every local row (sign-out). */
  clear(): Promise<void>;
}
