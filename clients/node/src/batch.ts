import type { PropValue } from './types.js';

export interface WireEvent {
  name: string;
  ts: number;
  props?: Record<string, PropValue>;
  tz?: number;
}

/** What groups events into one request. */
export interface BatchKey {
  installId: string;
  country?: string;
  /** Canonical JSON of the cleaned context, `{}` when there is none. */
  context: string;
  clientAddress?: string;
}

/** The key as a string: same installation, country, context and address. */
export function keyOf(key: BatchKey): string {
  return JSON.stringify([key.installId, key.country ?? null, key.context, key.clientAddress ?? null]);
}

/** Canonical JSON of a cleaned context: its keys are already sorted by `clean.props`. */
export function encodeContext(context: Record<string, PropValue>): string {
  return JSON.stringify(context);
}

/**
 * The wire shape of `POST /{source}`: `installId`, `country`, `context`, `events` and nothing else.
 * `JSON.stringify` drops undefined fields, which the collector reads as absent.
 */
export function encodeBatch(key: BatchKey, events: readonly WireEvent[]): string {
  return JSON.stringify({
    installId: key.installId,
    country: key.country,
    context: key.context.length > 2 ? (JSON.parse(key.context) as Record<string, PropValue>) : undefined,
    events: [...events]
      .sort((a, b) => a.ts - b.ts)
      .map((event) => ({
        name: event.name,
        ts: new Date(event.ts).toISOString(),
        props: event.props && Object.keys(event.props).length > 0 ? event.props : undefined,
        tz: event.tz,
      })),
  });
}
