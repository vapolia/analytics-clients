import { beforeEach, describe, expect, it } from 'vitest';

import { MAX_EVENT_AGE_MS } from '../core/clean';
import { Queue } from '../core/queue';
import { Spool } from '../core/spool';
import { resolveOptions, type Device, type ResolvedOptions } from '../core/types';
import { FakeCollector, MemoryStorage } from './support';

const installId = '11111111-0000-0000-0000-000011111111';

describe('Queue', () => {
  let now = 1_757_500_000_000;

  beforeEach(() => {
    now = 1_757_500_000_000;
  });

  function options(overrides: Partial<ResolvedOptions> = {}): ResolvedOptions {
    return {
      ...resolveOptions({ ingestionUrl: 'https://analytics.example.com/testsource' }),
      // Long enough that every test flushes explicitly.
      flushIntervalMs: 3_600_000,
      spoolDebounceMs: 5,
      ...overrides,
    };
  }

  function queue(
    collector: FakeCollector,
    overrides: Partial<ResolvedOptions> = {},
    spool?: Spool,
    id: () => string | undefined = () => installId
  ): Queue {
    return new Queue(options(overrides), collector, spool, id, () => now);
  }

  const device: Device = { country: 'FR' };

  it('sends one batch carrying the global properties', async () => {
    const collector = new FakeCollector();
    const sender = queue(collector);

    sender.track(device, 'app_open');
    sender.track(device, 'game_end', { result: 'win', moves: 34 });
    await sender.flush();

    expect(collector.bodies).toHaveLength(1);
    const batch = collector.batches[0]!;
    expect(batch.installId).toBe(installId);
    expect(batch.events.map((event) => event.name)).toEqual(['app_open', 'game_end']);
    expect(sender.getStats().sent).toBe(2);
  });

  it('groups by device context', async () => {
    const collector = new FakeCollector();
    const sender = queue(collector);

    sender.track(device, 'app_open');
    sender.track(device, 'game_start');
    sender.track({ country: 'DE' }, 'app_open');
    await sender.flush();

    expect(collector.bodies).toHaveLength(2);
  });

  it('sends as soon as a batch is full', async () => {
    const collector = new FakeCollector();
    const sender = queue(collector, { batchSize: 3 });

    sender.track(device, 'app_open');
    sender.track(device, 'app_open');
    sender.track(device, 'app_open');
    await sender.flush();

    expect(collector.bodies).toHaveLength(1);
    expect(collector.batches[0]!.events).toHaveLength(3);
  });

  it('refuses what the collector would drop', async () => {
    const collector = new FakeCollector();
    const sender = queue(collector, { excludedCountries: ['KR'] });

    sender.track({ country: 'KR' }, 'app_open');
    sender.track(device, '   ');
    await sender.flush();

    expect(collector.bodies).toHaveLength(0);
    expect(sender.getStats().rejected).toBe(2);
  });

  it('keeps events a transient failure left unsent', async () => {
    const collector = new FakeCollector([
      { kind: 'retry', afterMs: 0, reason: '500' },
      { kind: 'retry', afterMs: 0, reason: '500' },
    ]);
    const sender = queue(collector, { maxAttempts: 2 });

    sender.track(device, 'app_open');
    await sender.flush();

    expect(sender.getStats().requests).toBe(2);
    expect(sender.getStats().sent).toBe(0);
    expect(sender.getStats().dropped).toBe(0);

    // The next flush finds the collector back and sends the same event.
    await sender.flush();
    expect(sender.getStats().sent).toBe(1);
  });

  it('drops what a retry cannot fix', async () => {
    const collector = new FakeCollector([{ kind: 'permanent', reason: 'unknown source (404)' }]);
    const sender = queue(collector);

    sender.track(device, 'app_open');
    await sender.flush();

    expect(collector.bodies).toHaveLength(1);
    expect(sender.getStats().dropped).toBe(1);
  });

  it('drops events older than the collector accepts', async () => {
    const collector = new FakeCollector();
    const sender = queue(collector);

    sender.track(device, 'app_open');
    now += MAX_EVENT_AGE_MS + 1;
    await sender.flush();

    expect(collector.bodies).toHaveLength(0);
    expect(sender.getStats().dropped).toBe(1);
  });

  it('drops instead of blocking when the queue is full', async () => {
    const collector = new FakeCollector();
    const sender = queue(collector, { queueCapacity: 1, batchSize: 100 });

    for (let index = 0; index < 200; index += 1) sender.track(device, 'app_open');

    const stats = sender.getStats();
    expect(stats.accepted + stats.dropped).toBe(200);
    expect(stats.accepted).toBe(1);
  });

  it('keeps events tracked before storage answered, then sends them under the id', async () => {
    const collector = new FakeCollector();
    let id: string | undefined;
    const sender = queue(collector, {}, undefined, () => id);

    sender.track(device, 'app_open');
    await sender.flush();
    expect(collector.bodies).toHaveLength(0);

    id = installId;
    await sender.flush();
    expect(collector.batches[0]!.installId).toBe(installId);
  });

  it('spools what could not be sent and sends it on the next launch', async () => {
    const storage = new MemoryStorage();
    const failing = new FakeCollector([{ kind: 'retry', afterMs: 0, reason: 'offline' }]);
    const first = queue(failing, { maxAttempts: 1 }, new Spool(storage, 'testsource', 100));

    first.track(device, 'game_end', { result: 'win' });
    await first.stop();

    expect([...storage.items.keys()]).toContain('vapolia.analytics.spool.testsource');

    const collector = new FakeCollector();
    const restarted = queue(collector, {}, new Spool(storage, 'testsource', 100));
    await restarted.start();
    await restarted.flush();

    const batch = collector.batches[0]!;
    expect(batch.events[0]!.name).toBe('game_end');
    await restarted.stop();
  });
});
