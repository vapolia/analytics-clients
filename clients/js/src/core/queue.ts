import {
  MAX_CONTEXT_KEYS,
  MAX_EVENT_AGE_MS,
  MAX_VALUE_LENGTH,
  device as cleanDevice,
  props as cleanProps,
  text,
} from './clean';
import { batchKey, encodeBatch } from './batch';
import type { Poster } from './transport';
import type { Spool } from './spool';
import type {
  AnalyticsEvent,
  AnalyticsStats,
  Device,
  Pending,
  Props,
  PropValue,
  ResolvedOptions,
} from './types';

/** The sender shares the JS thread, so a long sleep would be paid in events never queued. */
const MAX_RETRY_DELAY_MS = 5_000;

/**
 * Buffers events, groups them per device context, and sends them.
 *
 * Nothing here touches React Native: the whole send path is plain TypeScript, which is what the unit
 * tests exercise under Node.
 */
export class Queue {
  private buffers = new Map<
    string,
    {
      device: Device;
      context?: Record<string, PropValue>;
      installId?: string;
      events: AnalyticsEvent[];
    }
  >();
  private held = 0;
  private stats: AnalyticsStats = { accepted: 0, rejected: 0, dropped: 0, sent: 0, requests: 0 };
  private timer: ReturnType<typeof setInterval> | undefined;
  private spoolTimer: ReturnType<typeof setTimeout> | undefined;
  private flushing: Promise<void> | undefined;
  private stopped = false;
  private windowStart = 0;
  private windowCount = 0;

  constructor(
    private readonly options: ResolvedOptions,
    private readonly poster: Poster,
    private readonly spool: Spool | undefined,
    private readonly resolveInstallId: () => string | undefined,
    private readonly now: () => number = () => Date.now()
  ) {}

  /** Reads back what the last run could not send, then starts the periodic flush. */
  async start(): Promise<void> {
    if (this.spool) {
      const items = await this.spool.load();
      for (const item of items) this.buffer(item);
    }

    this.timer = setInterval(() => {
      void this.flush();
    }, this.options.flushIntervalMs);
    // Node only: a pending interval must not keep a test process alive.
    (this.timer as unknown as { unref?: () => void }).unref?.();
  }

  /**
   * Queues one event. Never blocks, never throws, never reports an error. `getStats` is where losses
   * show up.
   */
  track(device: Device, name: string, props?: Props, context?: Props): void {
    if (this.stopped) return;

    if (!this.withinRate()) {
      this.stats.dropped += 1;
      return;
    }

    const eventName = text(name, MAX_VALUE_LENGTH);
    const cleanedDevice = cleanDevice(device, this.options.excludedCountries);
    if (eventName === undefined || cleanedDevice === undefined) {
      this.stats.rejected += 1;
      return;
    }

    if (this.held >= this.options.queueCapacity) {
      // Full queue: the collector is unreachable, or slower than we emit.
      this.stats.dropped += 1;
      return;
    }

    this.stats.accepted += 1;
    const ts = this.now();
    // Minutes east of UTC. getTimezoneOffset() answers the opposite — UTC minus local — hence the sign.
    const event: AnalyticsEvent = { name: eventName, ts, props: cleanProps(props), tz: -new Date(ts).getTimezoneOffset() };
    const cleanedContext = cleanProps(context, MAX_CONTEXT_KEYS);
    this.buffer({
      device: cleanedDevice,
      context: cleanedContext,
      event,
      installId: this.resolveInstallId(),
    });

    const group = this.buffers.get(batchKey(cleanedDevice, cleanedContext));
    if (group && group.events.length >= this.options.batchSize) {
      void this.flush();
    } else {
      this.scheduleSpool();
    }
  }

  /**
   * The client's own ceiling, counted in a fixed window: once it is full everything is dropped until
   * the window ends, rather than queued for a collector that would refuse the whole address.
   */
  private withinRate(): boolean {
    if (this.options.maxEventsPerWindow <= 0) return true;

    const now = this.now();
    if (now - this.windowStart >= this.options.rateWindowMs) {
      this.windowStart = now;
      this.windowCount = 0;
    }

    if (this.windowCount >= this.options.maxEventsPerWindow) return false;

    this.windowCount += 1;
    return true;
  }

  /** Sends every buffered group. Concurrent calls share the one in flight. */
  flush(): Promise<void> {
    if (this.flushing) return this.flushing;

    this.flushing = this.run().finally(() => {
      this.flushing = undefined;
    });
    return this.flushing;
  }

  /** Writes the queue down now, without waiting for the debounce. */
  async persist(): Promise<void> {
    this.clearSpoolTimer();
    if (!this.spool) return;
    await this.spool.save(this.pendingItems());
  }

  /** Drops everything held, storage included. Used when the user opts out. */
  async clear(): Promise<void> {
    this.clearSpoolTimer();
    this.buffers.clear();
    this.held = 0;
    await this.spool?.clear();
  }

  /** One last flush, then the queue stops. What it could not send is left in storage. */
  async stop(): Promise<void> {
    this.stopped = true;
    if (this.timer) clearInterval(this.timer);
    this.timer = undefined;

    await this.flush();
    await this.persist();
  }

  getStats(): AnalyticsStats {
    return { ...this.stats };
  }

  // MARK: - Internals

  private buffer(item: Pending): void {
    // Counted the same whether `track` produced it or it came back from storage: `held` bounds what
    // the client holds in memory, and both are held.
    this.held += 1;

    const key = batchKey(item.device, item.context);
    const group = this.buffers.get(key);
    if (group) {
      group.events.push(item.event);
      if (group.installId === undefined) group.installId = item.installId;
    } else {
      this.buffers.set(key, {
        device: item.device,
        context: item.context,
        installId: item.installId,
        events: [item.event],
      });
    }
  }

  private async run(): Promise<void> {
    const groups = [...this.buffers.values()];
    this.buffers.clear();

    for (const group of groups) {
      for (let index = 0; index < group.events.length; index += this.options.batchSize) {
        const chunk = group.events.slice(index, index + this.options.batchSize);
        const kept = await this.send(group.device, group.context, group.installId, chunk);
        this.keep(group.device, group.context, group.installId, kept);
      }
    }

    this.scheduleSpool();
  }

  /** Sends one group and accounts for it. Returns the events to try again later, if any. */
  private async send(
    device: Device,
    context: Record<string, PropValue> | undefined,
    installId: string | undefined,
    events: AnalyticsEvent[]
  ): Promise<AnalyticsEvent[]> {
    const cutoff = this.now() - MAX_EVENT_AGE_MS;
    const fresh = events.filter((event) => event.ts > cutoff);
    if (fresh.length < events.length) {
      // The collector refuses them on arrival, so this only saves the request.
      this.drop(events.length - fresh.length);
    }
    if (fresh.length === 0) return [];

    // An event tracked before storage answered has no id yet: the current one is the same
    // installation, since an id only ever rotates between launches.
    const id = installId ?? this.resolveInstallId();
    if (id === undefined) {
      // Keep them: the id is a launch away, and losing a session's first events is avoidable.
      return fresh;
    }

    const body = encodeBatch(id, device, fresh, context);

    for (let attempt = 1; ; attempt += 1) {
      this.stats.requests += 1;
      const result = await this.poster.post(body);

      if (result.kind === 'ok') {
        this.stats.sent += fresh.length;
        this.held -= fresh.length;
        return [];
      }

      if (result.kind === 'permanent') {
        this.options.logger?.error(`analytics: dropping ${fresh.length} events: ${result.reason}`);
        this.drop(fresh.length);
        return [];
      }

      if (attempt >= this.options.maxAttempts || this.stopped) {
        // Kept, not dropped: a failed send on a phone usually means no network.
        this.options.logger?.warn(`analytics: keeping ${fresh.length} events: ${result.reason}`);
        return fresh;
      }

      this.options.logger?.warn(`analytics: retrying ${fresh.length} events: ${result.reason}`);
      const delay = result.afterMs > 0 ? result.afterMs : 250 * 2 ** (attempt - 1);
      await sleep(Math.min(delay, MAX_RETRY_DELAY_MS));
    }
  }

  /** Puts back what a transient failure left unsent, unless the queue is already at its ceiling. */
  private keep(
    device: Device,
    context: Record<string, PropValue> | undefined,
    installId: string | undefined,
    events: AnalyticsEvent[]
  ): void {
    if (events.length === 0) return;

    const room = this.options.queueCapacity - this.bufferedCount();
    const kept = events.length > room ? events.slice(0, Math.max(0, room)) : events;
    if (kept.length < events.length) this.drop(events.length - kept.length);
    if (kept.length === 0) return;

    const key = batchKey(device, context);
    const group = this.buffers.get(key);
    if (group) group.events.push(...kept);
    else this.buffers.set(key, { device, context, installId, events: kept });
  }

  private drop(count: number): void {
    this.stats.dropped += count;
    this.held -= count;
  }

  private bufferedCount(): number {
    let total = 0;
    for (const group of this.buffers.values()) total += group.events.length;
    return total;
  }

  private pendingItems(): Pending[] {
    const items: Pending[] = [];
    for (const group of this.buffers.values()) {
      for (const event of group.events) {
        items.push({ device: group.device, event, installId: group.installId });
      }
    }
    return items;
  }

  /**
   * The debounce is what makes the spool the safety net: writing on every event would hammer storage,
   * writing only on background would miss a process killed while suspended.
   */
  private scheduleSpool(): void {
    if (!this.spool || this.stopped) return;

    this.clearSpoolTimer();
    this.spoolTimer = setTimeout(() => {
      this.spoolTimer = undefined;
      void this.spool?.save(this.pendingItems());
    }, this.options.spoolDebounceMs);
    (this.spoolTimer as unknown as { unref?: () => void }).unref?.();
  }

  private clearSpoolTimer(): void {
    if (this.spoolTimer) clearTimeout(this.spoolTimer);
    this.spoolTimer = undefined;
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, ms);
    (timer as unknown as { unref?: () => void }).unref?.();
  });
}
