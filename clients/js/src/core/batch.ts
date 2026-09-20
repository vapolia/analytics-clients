import type { AnalyticsEvent, Device, PropValue } from './types';

/**
 * The wire shape of `POST /{source}`.
 *
 * `JSON.stringify` drops undefined fields, which is exactly what the collector expects: an absent
 * field and a null one mean the same thing to it, and the absent one is shorter.
 */
export function encodeBatch(
  installId: string,
  device: Device,
  events: AnalyticsEvent[],
  context?: Record<string, PropValue>
): string {
  return JSON.stringify({
    installId,
    ...device,
    context: context && Object.keys(context).length > 0 ? context : undefined,
    // Sorted by timestamp: what leaves is then in the order it happened, whatever the buffer did.
    events: [...events]
      .sort((a, b) => a.ts - b.ts)
      .map((event) => ({
        name: event.name,
        ts: new Date(event.ts).toISOString(),
        props: Object.keys(event.props).length > 0 ? event.props : undefined,
        tz: event.tz,
      })),
  });
}

/**
 * The batch a group of events belongs to: same installation, same device, same context. Sorted keys,
 * so two identical contexts produce the same string and group together.
 */
export function batchKey(device: Device, context?: Record<string, PropValue>): string {
  return JSON.stringify([
    Object.keys(device)
      .sort()
      .map((key) => [key, device[key as keyof Device]]),
    Object.keys(context ?? {})
      .sort()
      .map((key) => [key, context?.[key]]),
  ]);
}
