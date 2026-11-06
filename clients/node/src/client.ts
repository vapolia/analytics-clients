import * as clean from './clean.js';
import { encodeBatch, encodeContext, keyOf, type BatchKey, type WireEvent } from './batch.js';
import { post } from './transport.js';
import type { AnalyticsOptions, AnalyticsStats, Device, Props, ResolvedOptions } from './types.js';

/** The sender shares the event loop with the caller, so a long sleep would be paid in buffered events. */
const MAX_RETRY_DELAY_MS = 5_000;

interface Bucket {
  key: BatchKey;
  events: WireEvent[];
}

function resolve(options: AnalyticsOptions): ResolvedOptions {
  const url = (options.ingestionUrl ?? '').trim().replace(/\/+$/, '');
  // The source is the URL's last segment, exactly as in the other clients.
  const source = url.slice(url.lastIndexOf('/') + 1);
  if (!/^https?:\/\/[^/]+\/.+/i.test(url) || source === '') {
    throw new TypeError('analytics: ingestionUrl must be https://baseUrl/sourceName');
  }

  const advanced = options.advanced ?? {};
  const positive = (value: number | undefined, fallback: number) =>
    value !== undefined && value > 0 ? value : fallback;

  return {
    url,
    source,
    token: options.token?.trim() || undefined,
    excludedCountries: (options.excludedCountries ?? []).map((c) => c.trim().toUpperCase()),
    context: options.context,
    flushIntervalMs: positive(advanced.flushIntervalMs, 30_000),
    maxEventsPerWindow: Math.max(0, advanced.maxEventsPerWindow ?? 0),
    rateWindowMs: positive(advanced.rateWindowMs, 60_000),
    batchSize: Math.min(positive(advanced.batchSize, clean.MAX_EVENTS_PER_BATCH), clean.MAX_EVENTS_PER_BATCH),
    queueCapacity: positive(advanced.queueCapacity, 4000),
    maxAttempts: positive(advanced.maxAttempts, 3),
    requestTimeoutMs: positive(advanced.requestTimeoutMs, 10_000),
    logger: advanced.logger,
    onError: advanced.onError,
    now: advanced.now ?? (() => Date.now()),
    fetch: advanced.fetch ?? fetch,
  };
}

/**
 * Buffers events per (install id, country, context, client address) and sends them in the
 * background. `track` never throws and never blocks.
 */
export class Client {
  private readonly options: ResolvedOptions;
  private readonly enabled: boolean;
  private readonly buffers = new Map<string, Bucket>();
  private readonly counters: AnalyticsStats = { accepted: 0, rejected: 0, dropped: 0, sent: 0, requests: 0 };
  /** Events buffered or waiting to be sent, against `queueCapacity`. */
  private held = 0;
  /** One request at a time, in order: a single sender keeps one installation's requests ordered. */
  private chain: Promise<void> = Promise.resolve();
  private timer: ReturnType<typeof setInterval> | undefined;
  private closing: Promise<void> | undefined;
  private wakeSleepers = new Set<() => void>();
  private windowStart = 0;
  private windowCount = 0;

  /** Throws a `TypeError` when `ingestionUrl` is not `https://baseUrl/sourceName`. */
  constructor(options: AnalyticsOptions) {
    this.options = resolve(options);
    this.enabled = options.isDebugBuild !== true;

    if (this.enabled) {
      this.timer = setInterval(() => this.flushAll(), this.options.flushIntervalMs);
      // The timer must not keep the process alive on its own.
      this.timer.unref?.();
    }
  }

  /** Queues one event. `installId` is the UUID the device generated. */
  track(installId: string, device: Device | undefined, name: string, props?: Props): void {
    this.enqueue(installId, device, undefined, undefined, undefined, name, props);
  }

  /** `track` with the batch context the app reported. */
  trackContext(installId: string, device: Device | undefined, context: Props | undefined, name: string, props?: Props): void {
    this.enqueue(installId, device, context, undefined, undefined, name, props);
  }

  /** A session for one installation, so call sites only carry the event. */
  for(installId: string, device?: Device): Session {
    return new Session(this, { installId, device });
  }

  /** Sends everything buffered so far and resolves once the collector answered. */
  flush(): Promise<void> {
    this.flushAll();
    return this.chain;
  }

  /** One last flush, then stops. Idempotent. Events tracked afterwards are ignored. */
  close(): Promise<void> {
    if (this.closing) return this.closing;

    if (this.timer) clearInterval(this.timer);
    this.timer = undefined;
    this.closing = Promise.resolve().then(() => {
      // A retry waiting on its backoff gets one last attempt now rather than after the delay.
      for (const wake of this.wakeSleepers) wake();
      this.flushAll();
      return this.chain;
    });
    return this.closing;
  }

  /** Drops what is buffered for this installation: the visitor opposed the measurement. */
  discard(installId: string): void {
    const id = clean.installId(installId);
    if (id === undefined) return;

    for (const [key, bucket] of this.buffers) {
      if (bucket.key.installId !== id) continue;
      this.buffers.delete(key);
      this.held -= bucket.events.length;
    }
  }

  stats(): AnalyticsStats {
    return { ...this.counters };
  }

  /** @internal The one path every `track` goes through. */
  enqueue(
    installId: string,
    device: Device | undefined,
    context: Props | undefined,
    tz: number | undefined,
    clientAddress: string | undefined,
    name: string,
    props: Props | undefined
  ): void {
    try {
      if (!this.enabled || this.closing) return;

      if (!this.withinRate()) {
        this.counters.dropped += 1;
        this.report(undefined, `dropping "${name}": the rate window is saturated`, false);
        return;
      }

      const id = clean.installId(installId);
      if (id === undefined) {
        this.counters.rejected += 1;
        this.report(undefined, `refusing "${name}": unusable install id`, true);
        return;
      }

      const eventName = clean.text(name);
      if (eventName === undefined) {
        this.counters.rejected += 1;
        this.report(undefined, 'refusing an event with an unusable name', true);
        return;
      }

      const cleanDevice = clean.device(device, this.options.excludedCountries);
      if (cleanDevice === undefined) {
        this.counters.rejected += 1;
        this.report(undefined, `refusing "${eventName}": excluded country`, true);
        return;
      }

      if (this.held >= this.options.queueCapacity) {
        this.counters.dropped += 1;
        this.report(undefined, `dropping "${eventName}": the queue is full`, false);
        return;
      }

      // The caller's own context wins. `options.context` is the fallback for a process-wide answer.
      const batchContext = context ?? this.options.context?.();
      const key: BatchKey = {
        installId: id,
        country: cleanDevice.country,
        context: encodeContext(clean.props(batchContext, clean.MAX_CONTEXT_KEYS)),
        clientAddress: clean.clientAddress(clientAddress),
      };

      const cleanProps = clean.props(props);
      const event: WireEvent = { name: eventName, ts: this.options.now() };
      if (Object.keys(cleanProps).length > 0) event.props = cleanProps;
      const offset = clean.tz(tz);
      if (offset !== undefined) event.tz = offset;

      const bucketId = keyOf(key);
      const bucket = this.buffers.get(bucketId) ?? { key, events: [] };
      bucket.events.push(event);
      this.buffers.set(bucketId, bucket);
      this.held += 1;
      this.counters.accepted += 1;

      if (bucket.events.length >= this.options.batchSize) {
        this.buffers.delete(bucketId);
        this.schedule(bucket);
      }
    } catch (error) {
      this.report(error, `dropping "${name}"`, true);
    }
  }

  /**
   * The client's own ceiling, counted in a fixed window. Zero, the default here, disables it.
   */
  private withinRate(): boolean {
    if (this.options.maxEventsPerWindow <= 0) return true;

    const now = this.options.now();
    if (now - this.windowStart >= this.options.rateWindowMs) {
      this.windowStart = now;
      this.windowCount = 0;
    }
    if (this.windowCount >= this.options.maxEventsPerWindow) return false;

    this.windowCount += 1;
    return true;
  }

  private flushAll(): void {
    const buckets = [...this.buffers.values()];
    this.buffers.clear();
    for (const bucket of buckets) this.schedule(bucket);
  }

  private schedule(bucket: Bucket): void {
    this.chain = this.chain.then(() =>
      this.send(bucket).catch((error) => {
        this.counters.dropped += bucket.events.length;
        this.report(error, `dropping ${bucket.events.length} events`, true);
      }).finally(() => {
        this.held -= bucket.events.length;
      })
    );
  }

  private async send(bucket: Bucket): Promise<void> {
    const cutoff = this.options.now() - clean.MAX_EVENT_AGE_MS;
    const events = bucket.events.filter((event) => event.ts > cutoff);
    this.counters.dropped += bucket.events.length - events.length;
    if (events.length === 0) return;

    const body = encodeBatch(bucket.key, events);
    for (let attempt = 1; ; attempt += 1) {
      this.counters.requests += 1;
      const result = await post(
        this.options.fetch,
        this.options.url,
        body,
        this.options.requestTimeoutMs,
        this.options.token,
        bucket.key.clientAddress
      );

      if (result.kind === 'ok') {
        this.counters.sent += events.length;
        return;
      }

      if (result.kind === 'permanent' || attempt >= this.options.maxAttempts) {
        this.counters.dropped += events.length;
        this.report(undefined, `dropping ${events.length} events after ${attempt} attempt(s): ${result.reason}`, true);
        return;
      }

      this.report(undefined, `retrying ${events.length} events (attempt ${attempt}): ${result.reason}`, false);
      const delay = Math.min(result.afterMs > 0 ? result.afterMs : 250 * 2 ** (attempt - 1), MAX_RETRY_DELAY_MS);
      if (!(await this.sleep(delay))) {
        // Closing: one last chance rather than sleeping through the shutdown.
        this.counters.requests += 1;
        const last = await post(
          this.options.fetch,
          this.options.url,
          body,
          this.options.requestTimeoutMs,
          this.options.token,
          bucket.key.clientAddress
        );
        if (last.kind === 'ok') this.counters.sent += events.length;
        else this.counters.dropped += events.length;
        return;
      }
    }
  }

  /** Resolves true after `ms`, or false at once when the client is closing. */
  private sleep(ms: number): Promise<boolean> {
    if (this.closing) return Promise.resolve(false);

    return new Promise((done) => {
      const wake = () => {
        clearTimeout(timer);
        this.wakeSleepers.delete(wake);
        done(false);
      };
      const timer = setTimeout(() => {
        this.wakeSleepers.delete(wake);
        done(true);
      }, ms);
      this.wakeSleepers.add(wake);
    });
  }

  private report(error: unknown, reason: string, permanent: boolean): void {
    const message = error === undefined ? `analytics: ${reason}` : `analytics: ${reason}: ${String(error)}`;
    try {
      if (permanent) this.options.logger?.error(message);
      else this.options.logger?.warn(message);
      this.options.onError?.(error, reason, permanent);
    } catch {
      // A failing logger must not reach the caller of track.
    }
  }
}

interface SessionState {
  installId: string;
  device: Device | undefined;
  context?: Props;
  tz?: number;
  clientAddress?: string;
}

/** An installation, its device, its batch context and its end client's address, bound once. */
export class Session {
  constructor(
    private readonly client: Client,
    private readonly state: SessionState
  ) {}

  /** The same session carrying a batch context. Its keys must be on the source's context whitelist. */
  withContext(context: Props): Session {
    return new Session(this.client, { ...this.state, context });
  }

  /** The same session carrying the user's offset, in minutes east of UTC, as the app reported it. */
  withTimeZone(minutes: number): Session {
    return new Session(this.client, { ...this.state, tz: minutes });
  }

  /**
   * The same session carrying the end client's IP address, sent as `X-Analytics-Client-Address`
   * under a token. An invalid address is not sent.
   */
  withClientAddress(address: string | undefined): Session {
    return new Session(this.client, { ...this.state, clientAddress: address });
  }

  track(name: string, props?: Props): void {
    const { installId, device, context, tz, clientAddress } = this.state;
    this.client.enqueue(installId, device, context, tz, clientAddress, name, props);
  }
}
