import type { AnalyticsStorage, Pending } from './types';

/**
 * What survives the process being killed — the common case on a phone, where a backgrounded app is
 * reclaimed without notice, and the JS engine is suspended within seconds. The queue is written here
 * a few hundred milliseconds after every event, not only on background.
 *
 * Events are read back once: `load` clears the key. A duplicated event is a wrong count, a lost one
 * only a missing count, so the ambiguity is resolved towards losing.
 */
export class Spool {
  private readonly key: string;

  constructor(
    private readonly storage: AnalyticsStorage,
    source: string,
    private readonly capacity: number
  ) {
    this.key = `vapolia.analytics.spool.${source}`;
  }

  async save(items: Pending[]): Promise<void> {
    if (items.length === 0) {
      await this.clear();
      return;
    }

    // Beyond the cap the oldest are kept: they are the ones a relaunch is meant to recover.
    const kept = items.length > this.capacity ? items.slice(0, this.capacity) : items;

    try {
      await this.storage.setItem(this.key, JSON.stringify({ version: 2, items: kept }));
    } catch {
      // Nothing to do about it, and nothing a caller could do either.
    }
  }

  async load(): Promise<Pending[]> {
    let raw: string | null = null;
    try {
      raw = await this.storage.getItem(this.key);
    } catch {
      return [];
    }

    await this.clear();
    if (!raw) return [];

    try {
      const parsed = JSON.parse(raw) as { version?: number; items?: Pending[] };
      if (parsed.version !== 2 || !Array.isArray(parsed.items)) return [];

      const items = parsed.items.filter(
        (item): item is Pending =>
          !!item &&
          typeof item === 'object' &&
          !!item.event &&
          typeof item.event.name === 'string' &&
          typeof item.event.ts === 'number'
      );
      return items.length > this.capacity ? items.slice(0, this.capacity) : items;
    } catch {
      // A truncated or older payload is not worth a migration path.
      return [];
    }
  }

  async clear(): Promise<void> {
    try {
      await this.storage.removeItem(this.key);
    } catch {
      // Same as above.
    }
  }
}
