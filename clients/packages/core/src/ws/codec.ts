import { create, fromJsonString, toJsonString } from '@bufbuild/protobuf';
import type { ClientFrame, ServerFrame } from '@talky/protocol/v1';
import { ClientFrameSchema, ServerFrameSchema } from '@talky/protocol/v1';

/**
 * The populated half of `ClientFrame.body` — i.e. every `oneof` case except "no body set".
 *
 * ```ts
 * client.send({ case: 'ping', value: create(PingSchema, { ts: 1n }) });
 * ```
 */
export type ClientFrameBody = Extract<ClientFrame['body'], { case: string }>;

/** The populated half of `ServerFrame.body`. */
export type ServerFrameBody = Extract<ServerFrame['body'], { case: string }>;

/**
 * Unknown fields are ignored so that a newer server can add fields without breaking older
 * clients (`docs/protocol.md` → "Kodlama").
 */
const READ_OPTIONS = { ignoreUnknownFields: true } as const;

/** Wrap a body in a `ClientFrame`. `rid` is empty for fire-and-forget frames. */
export function clientFrame(body: ClientFrameBody, rid = ''): ClientFrame {
  return create(ClientFrameSchema, { rid, body });
}

/** Encode a `ClientFrame` as the proto3-JSON text carried by one WebSocket text frame. */
export function encodeClientFrame(frame: ClientFrame): string {
  return toJsonString(ClientFrameSchema, frame);
}

/**
 * Decode one WebSocket text frame into a `ServerFrame`.
 *
 * @throws when the payload is not valid proto3 JSON for `talky.v1.ServerFrame`.
 */
export function decodeServerFrame(text: string): ServerFrame {
  return fromJsonString(ServerFrameSchema, text, READ_OPTIONS);
}

/** Decode a client frame. Used by mock servers and the golden-fixture replayer. */
export function decodeClientFrame(text: string): ClientFrame {
  return fromJsonString(ClientFrameSchema, text, READ_OPTIONS);
}

/** Encode a server frame. Used by mock servers and the golden-fixture replayer. */
export function encodeServerFrame(frame: ServerFrame): string {
  return toJsonString(ServerFrameSchema, frame);
}
