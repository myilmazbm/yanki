/** `WebSocket.readyState` values (RFC 6455 / WHATWG). */
export const WS_CONNECTING = 0;
/** @see WS_CONNECTING */
export const WS_OPEN = 1;
/** @see WS_CONNECTING */
export const WS_CLOSING = 2;
/** @see WS_CONNECTING */
export const WS_CLOSED = 3;

/** Subset of `MessageEvent` the client reads. */
export interface WebSocketMessageEventLike {
  /** Protocol v1 is text-only, so this is a `string` in practice. */
  readonly data: unknown;
}

/** Subset of `CloseEvent` the client reads. */
export interface WebSocketCloseEventLike {
  readonly code: number;
  readonly reason: string;
  readonly wasClean?: boolean;
}

/**
 * The smallest browser-compatible WebSocket surface `@talky/core` needs.
 *
 * The global `WebSocket` of browsers, Node ≥ 22, React Native and Tauri's webview all satisfy
 * this structurally, and so does the in-memory socket in `@talky/test-utils`.
 */
export interface WebSocketLike {
  readonly readyState: number;
  send(data: string): void;
  close(code?: number, reason?: string): void;
  onopen: ((event: unknown) => void) | null;
  onmessage: ((event: WebSocketMessageEventLike) => void) | null;
  onclose: ((event: WebSocketCloseEventLike) => void) | null;
  onerror: ((event: unknown) => void) | null;
}

/** Opens a connection. Injected so tests and exotic hosts can supply their own transport. */
export type WebSocketFactory = (url: string, protocols?: readonly string[]) => WebSocketLike;

type WebSocketConstructorLike = new (
  url: string,
  protocols?: string | readonly string[],
) => WebSocketLike;

/**
 * {@link WebSocketFactory} backed by the ambient global `WebSocket`.
 *
 * Throws when the host has no global `WebSocket` (older React Native without a polyfill,
 * Node < 22); such hosts must pass `webSocketFactory` explicitly.
 */
export const defaultWebSocketFactory: WebSocketFactory = (url, protocols) => {
  const ctor = (globalThis as { WebSocket?: WebSocketConstructorLike }).WebSocket;
  if (ctor === undefined) {
    throw new Error(
      'No global WebSocket is available; pass WsClientOptions.webSocketFactory explicitly.',
    );
  }
  return protocols === undefined ? new ctor(url) : new ctor(url, protocols);
};
