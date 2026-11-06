export type PropValue = string | number | boolean;
export type Props = Record<string, unknown>;

/**
 * What the body says about the device: its region, and nothing else. The platform and the build come
 * from the `Authorization` token.
 */
export interface Device {
  /** ISO 3166-1 alpha-2, from the device's region setting. Never a geolocation of the IP. */
  country?: string;
}

export interface Logger {
  warn(message: string): void;
  error(message: string): void;
}

export interface AnalyticsStats {
  /** Queued by `track`. */
  accepted: number;
  /** Refused by `track`: bad install id, excluded country, unusable event name. */
  rejected: number;
  /** Accepted then lost: full queue, stale event, or a request that failed every attempt. */
  dropped: number;
  /** Events the collector answered 2xx for. */
  sent: number;
  /** Requests issued, retries included. */
  requests: number;
}

export interface AnalyticsOptions {
  /** The collector's ingestion URL, `https://baseUrl/sourceName`. Required. */
  ingestionUrl: string;

  /**
   * The credential sent as `Authorization: Bearer`. A server token makes the collector throttle per
   * token instead of per address, and read `X-Analytics-Client-Address`.
   */
  token?: string;

  /** True measures nothing at all. Set it from the build configuration. */
  isDebugBuild?: boolean;

  /** ISO 3166-1 alpha-2 countries excluded from the collection. A copy of the collector's list. */
  excludedCountries?: readonly string[];

  /** What is true of the installation for a whole batch, asked for again on every event. */
  context?: () => Props | undefined;

  advanced?: AdvancedOptions;
}

export interface AdvancedOptions {
  /** How long events are buffered before they go out. Default 30 s. */
  flushIntervalMs?: number;
  /** Events accepted per `rateWindowMs`. Default 0, disabled: one process speaks for every visitor. */
  maxEventsPerWindow?: number;
  /** The window `maxEventsPerWindow` counts in. Fixed, not sliding. Default 60 s. */
  rateWindowMs?: number;
  /** Events of one group sent in one request. Default and maximum 100. */
  batchSize?: number;
  /** Events buffered at most. Beyond it `track` drops. Default 4000. */
  queueCapacity?: number;
  /** Attempts per request before its events are dropped. Default 3. */
  maxAttempts?: number;
  /** Timeout of one request. Default 10 s. */
  requestTimeoutMs?: number;
  logger?: Logger;
  /** Called on every loss, next to the log. */
  onError?: (error: unknown, reason: string, permanent: boolean) => void;
  /** Defaults to `Date.now`. Tests set it. */
  now?: () => number;
  /** Defaults to the global `fetch`. Tests set it. */
  fetch?: typeof fetch;
}

export interface ResolvedOptions {
  url: string;
  source: string;
  token: string | undefined;
  excludedCountries: string[];
  context: (() => Props | undefined) | undefined;
  flushIntervalMs: number;
  maxEventsPerWindow: number;
  rateWindowMs: number;
  batchSize: number;
  queueCapacity: number;
  maxAttempts: number;
  requestTimeoutMs: number;
  logger: Logger | undefined;
  onError: ((error: unknown, reason: string, permanent: boolean) => void) | undefined;
  now: () => number;
  fetch: typeof fetch;
}
