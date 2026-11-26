import type { Logger } from '../logger.js';
import type { Storage } from '../storage/Storage.js';
import type { TimerHost } from '../time.js';
import type { WebSocketFactory } from '../ws/WebSocketLike.js';
import type { ClientDescriptor } from '../ws/types.js';

/**
 * Everything host-specific that `@yanki/core` may need, in one injected bundle.
 *
 * DRAFT contract, finalised together with `Storage` by the M2 sync engine. No adapters exist
 * yet; the planned ones are:
 *
 * | host    | storage             | websocket                     | notifications          |
 * |---------|---------------------|-------------------------------|------------------------|
 * | web     | IndexedDB           | global `WebSocket`            | Notification API       |
 * | Tauri   | SQLite plugin       | global `WebSocket` (webview)  | `@tauri-apps/plugin-notification` |
 * | RN      | op-sqlite / expo-sqlite | RN `WebSocket`            | Notifee / FCM / APNs   |
 *
 * Nothing in this interface is imported by `@yanki/core` at runtime — it exists so that hosts
 * have one thing to implement and one thing to hand over.
 */
export interface Platform {
  /** Static description of the app, forwarded in `Hello.client`. */
  readonly info: ClientDescriptor;
  /** Stable per-install device id (UUID). Persisted by the host. */
  readonly deviceId: string;
  readonly storage: Storage;
  readonly timers: TimerHost;
  readonly logger: Logger;
  /** Opens realtime connections. */
  readonly createWebSocket: WebSocketFactory;
  /** Connectivity signal, when the host can provide one. */
  readonly network?: NetworkMonitor;
  /** Local notification sink, when the host can provide one. */
  readonly notifications?: NotificationSink;
}

/** Coarse connectivity signal used to skip pointless reconnect attempts. */
export interface NetworkMonitor {
  readonly online: boolean;
  /**
   * Observe connectivity changes.
   *
   * @returns an unsubscribe function.
   */
  subscribe(listener: (online: boolean) => void): () => void;
}

/** A local (non-push) notification. */
export interface LocalNotification {
  title: string;
  body: string;
  /** Opaque routing payload handed back when the user activates the notification. */
  data?: Record<string, string>;
}

/** Host-provided local notification sink. */
export interface NotificationSink {
  show(notification: LocalNotification): Promise<void>;
}
