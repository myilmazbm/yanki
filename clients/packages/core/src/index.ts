/**
 * `@talky/core` — the platform-agnostic Talky client.
 *
 * Everything here runs unchanged on the React web app, the Tauri desktop shell and React
 * Native: there is no DOM, no Node built-in and no UI framework anywhere in this package.
 * Host specifics arrive through injected seams ({@link WebSocketFactory}, {@link TimerHost},
 * {@link Storage}, {@link Platform}).
 *
 * @see docs/client-core.md
 * @see docs/protocol.md — the normative behaviour this package implements.
 */

// Realtime client
export { createWsClient } from './ws/client.js';
export type {
  Ack,
  ClientDescriptor,
  ClientPlatform,
  ReconnectReason,
  ReconnectScheduled,
  RequestOptions,
  ResolvedWsClientOptions,
  WsClient,
  WsClientEvents,
  WsClientOptions,
  WsClientState,
} from './ws/types.js';

// Transport seam
export {
  defaultWebSocketFactory,
  WS_CLOSED,
  WS_CLOSING,
  WS_CONNECTING,
  WS_OPEN,
} from './ws/WebSocketLike.js';
export type {
  WebSocketCloseEventLike,
  WebSocketFactory,
  WebSocketLike,
  WebSocketMessageEventLike,
} from './ws/WebSocketLike.js';

// Wire codec
export {
  clientFrame,
  decodeClientFrame,
  decodeServerFrame,
  encodeClientFrame,
  encodeServerFrame,
} from './ws/codec.js';
export type { ClientFrameBody, ServerFrameBody } from './ws/codec.js';

// Reconnect + de-duplication primitives
export { backoffDelayMs, DEFAULT_BACKOFF } from './ws/backoff.js';
export type { BackoffOptions } from './ws/backoff.js';
export { createFrameDeduper } from './ws/dedupe.js';
export type { DedupeKeyable, FrameDeduper } from './ws/dedupe.js';

// Errors and events
export { isTalkyWsError, TalkyWsError } from './errors.js';
export type { TalkyWsErrorInit, TalkyWsErrorReason } from './errors.js';
export { createEmitter } from './events.js';
export type { Emitter, EventMap, EventSubscriber, Listener } from './events.js';

// Clock, ids, logging
export { systemTimerHost } from './time.js';
export type { TimerHandle, TimerHost } from './time.js';
export { createUuidV7, isUuidV7, uuidv7, UUID_V7_PATTERN } from './uuid.js';
export type { UuidV7Deps } from './uuid.js';
export { noopLogger } from './logger.js';
export type { Logger, LogFields } from './logger.js';

// Persistence and host contracts
export { MemoryStorage } from './storage/MemoryStorage.js';
export type {
  ConversationStore,
  Draft,
  DraftStore,
  KvStore,
  MessageState,
  MessageStore,
  OutboxEntry,
  OutboxStore,
  SeqRange,
  Storage,
  StoredConversation,
  StoredMessage,
} from './storage/Storage.js';
export type {
  LocalNotification,
  NetworkMonitor,
  NotificationSink,
  Platform,
} from './platform/Platform.js';
