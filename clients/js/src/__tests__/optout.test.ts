import { describe, expect, it } from 'vitest';

import { Queue } from '../core/queue';
import { Spool } from '../core/spool';
import { resolveOptions, type Device, type ResolvedOptions } from '../core/types';
import type { Poster, SendResult } from '../core/transport';
import { MemoryStorage } from './support';

const installId = '11111111-0000-0000-0000-000011111111';
const device: Device = { country: 'FR' };

/** A collector that holds every request open until the test lets it answer. */
class GatedCollector implements Poster {
  readonly bodies: string[] = [];
  private release!: () => void;
  readonly entered: Promise<void>;
  private enter!: () => void;

  constructor(private readonly answer: SendResult) {
    this.entered = new Promise((resolve) => {
      this.enter = resolve;
    });
  }

  async post(body: string): Promise<SendResult> {
    this.bodies.push(body);
    this.enter();
    await new Promise<void>((resolve) => {
      this.release = resolve;
    });
    return this.answer;
  }

  open(): void {
    this.release();
  }
}

function options(overrides: Partial<ResolvedOptions> = {}): ResolvedOptions {
  return {
    ...resolveOptions({ ingestionUrl: 'https://analytics.example.com/testsource' }),
    flushIntervalMs: 3_600_000,
    spoolDebounceMs: 5,
    // One attempt: a retry would call `post` again and park on a gate the test already opened.
    maxAttempts: 1,
    ...overrides,
  };
}

describe('opting out while a request is in flight', () => {
  it('does not put the in-flight events back in the queue', async () => {
    // Kept, not dropped: this is exactly the case where `keep` re-buffers.
    const collector = new GatedCollector({ kind: 'retry', afterMs: 0, reason: 'offline' });
    const storage = new MemoryStorage();
    const spool = new Spool(storage, 'testsource', 100);
    const queue = new Queue(options(), collector, spool, () => installId);

    queue.track(device, 'app_open');
    const flushing = queue.flush();
    await collector.entered;

    // Issued while the send is parked inside `post`.
    const clearing = queue.clear();
    collector.open();
    await flushing;
    await clearing;

    // A second flush proves the buffers are empty: nothing new reaches the collector.
    await queue.flush();
    expect(collector.bodies).toHaveLength(1);
    expect(queue.getStats().sent).toBe(0);
  });

  it('does not write the in-flight events back to storage', async () => {
    const collector = new GatedCollector({ kind: 'retry', afterMs: 0, reason: 'offline' });
    const storage = new MemoryStorage();
    const spool = new Spool(storage, 'testsource', 100);
    const queue = new Queue(options(), collector, spool, () => installId);

    queue.track(device, 'app_open');
    const flushing = queue.flush();
    await collector.entered;

    const clearing = queue.clear();
    collector.open();
    await flushing;
    await clearing;

    // The debounced write would otherwise land after the clear and resurrect the event.
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(await spool.load()).toEqual([]);
  });
});

describe('the spool', () => {
  it('carries the batch context across a process death', async () => {
    const collector = new GatedCollector({ kind: 'retry', afterMs: 0, reason: 'offline' });
    const storage = new MemoryStorage();
    const spool = new Spool(storage, 'testsource', 100);
    const queue = new Queue(options(), collector, spool, () => installId);

    queue.track(device, 'game_end', { result: 'win' }, { plan: 'premium' });
    await queue.persist();

    const items = await spool.load();
    expect(items).toHaveLength(1);
    // `context` is part of what groups events into one request: losing it here would merge the
    // event into whatever context the next launch happens to have.
    expect(items[0]?.context).toEqual({ plan: 'premium' });
    expect(items[0]?.device).toEqual(device);
  });
});
