/** A property value. The collector only ever accepts scalars. */
export type PropValue = string | number | boolean;

export type Props = Record<string, PropValue | null | undefined>;

/**
 * What the body still says about the device: its region, and nothing else.
 *
 * The platform, the build, the OS version, the device class and the store come from the
 * `Authorization` token, which names the build that was issued it — so no client sends them.
 * `country` stays because it is the axis of the source's `excludedCountries` filter. Same shape as
 * the .NET client's `Device` record.
 */
export interface Device {
  /** ISO 3166-1 alpha-2, from the device's **region setting** — never a geolocation of the IP. */
  country?: string;
}

/**
 * An installation identity that comes from somewhere else — a client being migrated off another
 * SDK, which already has an id and a first-seen date worth keeping.
 */
export interface InstallSeed {
  installId: string;
  /** Milliseconds since the epoch. */
  issuedAt: number;
  /** Milliseconds since the epoch. */
  firstSeen: number;
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
  /**
   * Lost rather than sent: a saturated rate window, a full queue, a full spool, an event older than
   * the collector accepts, or a permanently refused request.
   */
  dropped: number;
  /** Events the collector answered 2xx for. */
  sent: number;
  /** Requests issued, retries included. */
  requests: number;
}

/**
 * Client configuration. `ingestionUrl` is the only thing without a default.
 *
 * The shape mirrors the .NET client, which is this repository's reference: the few options an app
 * actually sets sit here, the rest in `advanced` and `app`.
 */
export interface AnalyticsOptions {
  /**
   * The collector's ingestion URL — `https://baseUrl/sourceName`. The last path segment is the
   * source: the app's name, and the Postgres schema it maps to.
   *
   * Required: a default here would live in the published package, so moving the collector would mean
   * republishing it and waiting for every app to update before the old host could go.
   */
  ingestionUrl: string;

  /**
   * The credential sent as `Authorization: Bearer` — a server token for server-to-server analytics,
   * or a build token for device-to-server analytics. The collector tells the two apart from the
   * token itself, not from how it arrived, so one property covers both roles.
   */
  token?: string;

  /**
   * The identity of this installation, when it comes from somewhere else — a client being migrated
   * off another SDK. Read once, at startup, and only if nothing is stored yet.
   */
  seedInstallId?: () => InstallSeed | undefined;

  /**
   * True measures nothing at all: `start` is a no-op, and nothing restarts the client before the
   * process does. Set it from the build configuration. The person's own switch is `setOptedOut`,
   * which takes effect at once and either way.
   */
  isDebugBuild?: boolean;

  /**
   * What `isOptedOut()` answers while the person has not answered. True sends nothing and writes no
   * installation id until the welcome popup calls `setOptedOut(false)`.
   *
   * Null reads the device locale and answers from `localeRequiresPriorConsent`.
   */
  requiresPriorConsent?: boolean | null;

  /**
   * ISO 3166-1 alpha-2 countries excluded from the collection. Should be a copy of the exclusion
   * list of the collector (the analytics server).
   */
  excludedCountries?: string[];

  /**
   * Context sent with each batch of events, asked for again on every event: these change while the
   * app runs, and an event must carry the state it was produced under. Every key must be on the
   * source's `context` whitelist.
   */
  context?: () => Context | undefined;

  /** Uncommon options. */
  advanced?: AnalyticsAdvancedOptions;

  /** Uncommon apps-only options. */
  app?: AnalyticsAppOptions;
}

/** Uncommon options. The defaults are the ones every client in this repository ships. */
export interface AnalyticsAdvancedOptions {
  /** How long events are buffered before they go out. */
  flushIntervalMs?: number;

  /**
   * Max events accepted per `rateWindowMs`. Beyond it everything is dropped until the window ends.
   * Zero disables it. It keeps a buggy installation from getting its whole client address throttled
   * by the collector, which behind a carrier NAT would hit every other installation.
   */
  maxEventsPerWindow?: number;

  /** The window `maxEventsPerWindow` counts in. Fixed, not sliding. */
  rateWindowMs?: number;

  /** Max events to send per batch. Capped at the collector's own ceiling. */
  batchSize?: number;

  /** Max events that can wait to be sent. Beyond it `track` drops rather than blocks. */
  queueCapacity?: number;

  /** Max attempts to send a batch before it is given up on. */
  maxAttempts?: number;

  requestTimeoutMs?: number;

  /** Events kept in storage across process death. Zero disables the spool. */
  spoolCapacity?: number;

  /**
   * How long after an event the queue is written to storage. The JS engine is suspended shortly
   * after the app backgrounds, so the spool — not the background flush — is what actually survives.
   * This client's own option: it stands in for the `beginBackgroundTask` it does not have.
   */
  spoolDebounceMs?: number;

  /** 13 months minus a margin for clock drift — the legal ceiling, with no extension. */
  installIdLifetimeMs?: number;

  /** How long a refusal is remembered. Unlike the identifier, it is refreshed on every visit. */
  optOutLifetimeMs?: number;

  /**
   * The device context. Only `country` is sent; what is not given here is detected when
   * `expo-localization` is installed.
   */
  device?: Device;

  logger?: AnalyticsLogger;

  /**
   * Called on every loss, next to the log: `(error, reason, permanent)`. Can be used to notify the
   * user that an issue is ongoing.
   */
  onError?: (error: unknown, reason: string, permanent: boolean) => void;
}

/** Uncommon apps-only options. */
export interface AnalyticsAppOptions {
  /**
   * Whether the client flushes and spools when the app goes to the background. On a JS runtime this
   * is best effort — the engine is suspended shortly after — which is what `spoolDebounceMs` covers.
   */
  flushesOnBackground?: boolean;
}

/** The flat shape the internals work with, with every default already applied. */
export interface ResolvedOptions {
  ingestionUrl: string;
  source: string;
  token: string;
  seedInstallId?: () => InstallSeed | undefined;
  isDebugBuild: boolean;
  requiresPriorConsent: boolean | null;
  excludedCountries: string[];
  context: () => Context | undefined;
  flushIntervalMs: number;
  maxEventsPerWindow: number;
  rateWindowMs: number;
  batchSize: number;
  queueCapacity: number;
  maxAttempts: number;
  requestTimeoutMs: number;
  spoolCapacity: number;
  spoolDebounceMs: number;
  installIdLifetimeMs: number;
  optOutLifetimeMs: number;
  device: Device;
  logger?: AnalyticsLogger;
  onError?: (error: unknown, reason: string, permanent: boolean) => void;
  flushesOnBackground: boolean;
}

const DAY_MS = 24 * 60 * 60 * 1000;

export function resolveOptions(options: AnalyticsOptions): ResolvedOptions {
  const advanced = options.advanced ?? {};
  const app = options.app ?? {};

  return {
    ingestionUrl: options.ingestionUrl.replace(/\/+$/, ''),
    // The app's name is the URL's last segment, exactly as in the .NET client.
    source: options.ingestionUrl.replace(/\/+$/, '').split('/').pop() ?? '',
    token: options.token ?? '',
    seedInstallId: options.seedInstallId,
    isDebugBuild: options.isDebugBuild ?? false,
    requiresPriorConsent: options.requiresPriorConsent ?? null,
    excludedCountries: options.excludedCountries ?? [],
    context: options.context ?? (() => undefined),
    flushIntervalMs: advanced.flushIntervalMs ?? 30_000,
    maxEventsPerWindow: advanced.maxEventsPerWindow ?? 30,
    rateWindowMs: advanced.rateWindowMs ?? 60_000,
    batchSize: Math.min(advanced.batchSize ?? 100, 100),
    queueCapacity: advanced.queueCapacity ?? 4_000,
    maxAttempts: advanced.maxAttempts ?? 3,
    requestTimeoutMs: advanced.requestTimeoutMs ?? 10_000,
    spoolCapacity: advanced.spoolCapacity ?? 1_000,
    spoolDebounceMs: advanced.spoolDebounceMs ?? 500,
    installIdLifetimeMs: advanced.installIdLifetimeMs ?? 390 * DAY_MS,
    optOutLifetimeMs: advanced.optOutLifetimeMs ?? 390 * DAY_MS,
    device: advanced.device ?? {},
    logger: advanced.logger,
    onError: advanced.onError,
    flushesOnBackground: app.flushesOnBackground ?? true,
  };
}
