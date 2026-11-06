import { isIP } from 'node:net';
import type { Device, Props, PropValue } from './types.js';

// Limits mirrored from the collector's EventSanitizer, which applies them again on arrival.
export const MAX_EVENTS_PER_BATCH = 100;
export const MAX_PROPS_PER_EVENT = 12;
export const MAX_CONTEXT_KEYS = 12;
export const MAX_VALUE_LENGTH = 64;

/** How far back the collector accepts a timestamp. Older events are dropped instead of sent. */
export const MAX_EVENT_AGE_MS = 7 * 24 * 60 * 60 * 1000;

// The C0 and C1 control ranges. Postgres rejects U+0000 in `text` and `jsonb`.
// eslint-disable-next-line no-control-regex
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f-\u009f]/g;

/** Trims, caps the length, strips control characters, and turns blank into undefined. */
export function text(value: unknown, maxLength: number = MAX_VALUE_LENGTH): string | undefined {
  if (typeof value !== 'string') return undefined;

  const trimmed = value.trim();
  if (trimmed.length === 0) return undefined;

  // Never half an emoji: a lone surrogate reaches the collector as U+FFFD.
  const last = trimmed.charCodeAt(maxLength - 1);
  const cut = last >= 0xd800 && last <= 0xdbff ? maxLength - 1 : maxLength;
  const capped = trimmed.length > maxLength ? trimmed.slice(0, cut) : trimmed;
  const cleaned = capped.replace(CONTROL_CHARACTERS, '').trim();
  return cleaned.length === 0 ? undefined : cleaned;
}

/** Exactly two ASCII letters, or nothing: truncating would turn "GERMANY" into "GE", which is Georgia. */
export function country(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;

  const trimmed = value.trim();
  return /^[a-z]{2}$/i.test(trimmed) ? trimmed.toUpperCase() : undefined;
}

/** The canonical UUID form only, and never the nil UUID. */
export function installId(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;

  const id = value.trim().toLowerCase();
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(id)) return undefined;
  if (/^[0-]+$/.test(id)) return undefined; // the nil UUID

  return id;
}

/** The device reduced to what the collector will store, or undefined when the batch is refused. */
export function device(value: Device | undefined, excludedCountries: readonly string[] = []): Device | undefined {
  const region = country(value?.country);
  if (region !== undefined && excludedCountries.includes(region)) return undefined;
  return region === undefined ? {} : { country: region };
}

/** Scalars only, capped in count and in length. Keys are cleaned like the values. */
export function props(value: Props | undefined, maxKeys: number = MAX_PROPS_PER_EVENT): Record<string, PropValue> {
  if (!value || typeof value !== 'object') return {};

  const kept: Record<string, PropValue> = {};
  let count = 0;
  // Sorted so what survives the cap does not depend on the insertion order, and so identical
  // contexts serialise identically.
  for (const key of Object.keys(value).sort()) {
    if (count === maxKeys) break;

    const item = value[key];
    const cleanedKey = text(key);
    if (cleanedKey === undefined) continue;

    let cleaned: PropValue | undefined;
    if (typeof item === 'string') cleaned = text(item);
    else if (typeof item === 'boolean') cleaned = item;
    // NaN and the infinities are not representable in jsonb.
    else if (typeof item === 'number' && Number.isFinite(item)) cleaned = item;

    if (cleaned !== undefined && !(cleanedKey in kept)) {
      kept[cleanedKey] = cleaned;
      count += 1;
    }
  }

  return kept;
}

/** An offset within UTC−12:00..UTC+14:00, in whole minutes, or undefined. */
export function tz(value: unknown): number | undefined {
  if (typeof value !== 'number' || !Number.isInteger(value)) return undefined;
  return value >= -12 * 60 && value <= 14 * 60 ? value : undefined;
}

/**
 * One IPv4 or IPv6 address, or undefined. An IPv4-mapped IPv6 address becomes its IPv4 form, and an
 * IPv6 zone is removed.
 */
export function clientAddress(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;

  let address = value.trim().toLowerCase();
  const zone = address.indexOf('%');
  if (zone >= 0) address = address.slice(0, zone);

  const mapped = /^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/.exec(address);
  if (mapped?.[1] !== undefined) address = mapped[1];

  return isIP(address) === 0 ? undefined : address;
}
