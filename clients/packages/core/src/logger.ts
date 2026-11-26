/** Structured fields attached to a log line. Never contains user content. */
export type LogFields = Record<string, unknown>;

/**
 * Minimal logging seam. `@yanki/core` never touches `console` directly so that host apps can
 * route diagnostics into their own sink (web console, Tauri log plugin, RN Flipper, …).
 */
export interface Logger {
  debug(message: string, fields?: LogFields): void;
  info(message: string, fields?: LogFields): void;
  warn(message: string, fields?: LogFields): void;
  error(message: string, fields?: LogFields): void;
}

/** Default logger: drops everything. */
export const noopLogger: Logger = {
  debug() {
    /* no-op */
  },
  info() {
    /* no-op */
  },
  warn() {
    /* no-op */
  },
  error() {
    /* no-op */
  },
};
