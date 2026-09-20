/** A property value. The collector only ever accepts scalars. */
export type PropValue = string | number | boolean;

export type Props = Record<string, PropValue | null | undefined>;

/**
 * Facts about the device, which the collector stores per event but which only change between
 * launches. Every field is optional, and the client fills most of them in. What is true of the
 * installation rather than of the device belongs in the batch `Context` instead.
 */
export interface Device {
  /** Store build number, not the display version. */
  build?: string;
  /** android | ios | maccatalyst | windows | web */
  platform?: string;
  osVersion?: string;
  /** phone | tablet | desktop | other */
  deviceClass?: string;
  locale?: string;
  /** ISO 3166-1 alpha-2, from the device locale. */
  country?: string;
  /** google | apple | other */
  store?: string;
}

/**
 * What is true of the installation for a whole batch. Plain keys and scalars: the client names none
 * of them, and every key must be on the source's `context` whitelist.
 */
export type Context = Props;

export interface AnalyticsEvent {
  name: string;
  /** Milliseconds since the epoch, captured when the event was tracked. */
  ts: number;
  props: Record<string, PropValue>;
  /** Minutes east of UTC when the event was tracked, apart from `ts` which is UTC. Absent when unknown. */
  tz?: number;
}

export interface Pending {
  device: Device;
  /** The batch context, already cleaned. Part of what groups events into one request. */
  context?: Record<string, PropValue>;
  event: AnalyticsEvent;
  /**
   * Absent for an event tracked before storage answered. It is filled in when the event leaves, and
   * carried through the spool so a rotation between launches cannot re-attribute old events.
   */
  installId?: string;
}

/** Where transport failures go. Nothing else is ever reported. */
export interface AnalyticsLogger {
  warn(message: string): void;
  error(message: string): void;
}

/**
 * The key/value store the client persists to. `@react-native-async-storage/async-storage` satisfies
 * it as is; the tests pass an in-memory one.
 */
export interface AnalyticsStorage {
  getItem(key: string): Promise<string | null>;
  setItem(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
}

/** A snapshot of what the client did since it was created. */
export interface AnalyticsStats {
  /** Queued by `track`. */
  accepted: number;
  /** Refused by `track`: opted out, excluded country, unusable event name. */
  rejected: number;
  /** Accepted then lost: full queue, or a permanently refused request. */
  dropped: number;
  /** Events the collector answered 2xx for. */
  sent: number;
  /** Requests issued, retries included. */
  requests: number;
}

export interface AnalyticsOptions {
  /** The app's name: the URL segment, and the Postgres schema it maps to. */
  source: string;

  /**
   * The collector's base URL, e.g. "https://analytics.example.com". The source is appended to it.
   * Required: a default here would live in the published package, so moving the collector would mean
   * republishing it and waiting for every app to update before the old host could go.
   */
  endpoint: string;

  /**
   * The token issued for this build by the collector's admin service, as a CI step, and embedded in
   * the app. Sent as `Authorization: Bearer`, it replaces the platform and build number in every
   * batch, so a build cannot be invented and can be excluded. Public by nature, not a credential. A
   * source that requires one answers 401 without.
   */
  token?: string;

  /**
   * ISO 3166-1 alpha-2 countries not measured at all: nothing is sent from a device whose region is
   * one of them. Copy the source's `excludedCountries`.
   */
  excludedCountries?: string[];

  /** How often pending events are sent even when no batch is full. Milliseconds. */
  flushIntervalMs?: number;

  /** Events of one device context per request. Capped at the collector's own ceiling. */
  batchSize?: number;

  /** Events waiting for the sender. Beyond it `track` drops rather than blocks. */
  queueCapacity?: number;

  /**
   * Events accepted per `rateWindowMs`. Beyond it everything is dropped until the window ends.
   *
   * An installation emitting hundreds of events a minute is a bug, and queueing them would get its
   * whole client address throttled by the collector — which on a carrier-NAT address means every
   * other installation behind it too. Zero disables it.
   */
  maxEventsPerWindow?: number;

  /** The window `maxEventsPerWindow` counts in. Fixed, not sliding. */
  rateWindowMs?: number;

  /** Events kept in storage across process death. */
  spoolCapacity?: number;

  /**
   * How long after an event the queue is written to storage. The JS engine is suspended shortly
   * after the app backgrounds, so the spool — not the background flush — is what actually survives.
   */
  spoolDebounceMs?: number;

  /** Attempts per request before a batch is given up on (kept for later if the failure was transient). */
  maxAttempts?: number;

  requestTimeoutMs?: number;

  /**
   * What is true of the installation for a whole batch, asked for again on every event: these change
   * while the app runs, and an event must carry the state it was produced under.
   */
  context?: () => Context | undefined;

  /**
   * The device context. What is not given here is detected, when the matching `expo-*` package is
   * installed. Pass it in full to depend on no Expo package at all.
   */
  device?: Device;

  logger?: AnalyticsLogger;
}

export type ResolvedOptions = Required<
  Omit<AnalyticsOptions, 'device' | 'logger'>
> & {
  device: Device;
  logger?: AnalyticsLogger;
};

export function resolveOptions(options: AnalyticsOptions): ResolvedOptions {
  return {
    source: options.source,
    endpoint: options.endpoint,
    token: options.token ?? '',
    excludedCountries: options.excludedCountries ?? [],
    flushIntervalMs: options.flushIntervalMs ?? 30_000,
    batchSize: Math.min(options.batchSize ?? 100, 100),
    queueCapacity: options.queueCapacity ?? 2_000,
    maxEventsPerWindow: options.maxEventsPerWindow ?? 30,
    rateWindowMs: options.rateWindowMs ?? 60_000,
    spoolCapacity: options.spoolCapacity ?? 1_000,
    spoolDebounceMs: options.spoolDebounceMs ?? 500,
    maxAttempts: options.maxAttempts ?? 3,
    requestTimeoutMs: options.requestTimeoutMs ?? 10_000,
    context: options.context ?? (() => undefined),
    device: options.device ?? {},
    logger: options.logger,
  };
}
