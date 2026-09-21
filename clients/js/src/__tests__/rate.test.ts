import { describe, expect, it } from 'vitest';

import { Queue } from '../core/queue';
import { resolveOptions, type Device, type ResolvedOptions } from '../core/types';
import { FakeCollector } from './support';

const installId = '11111111-0000-0000-0000-000011111111';
const device: Device = {};

describe('the per-window ceiling', () => {
  let now = 1_757_500_000_000;

  function queue(maxEventsPerWindow: number): Queue {
    const options: ResolvedOptions = {
      ...resolveOptions({ ingestionUrl: 'https://analytics.example.com/testsource' }),
      flushIntervalMs: 3_600_000,
      maxEventsPerWindow,
      rateWindowMs: 60_000,
    };
    return new Queue(options, new FakeCollector(), undefined, () => installId, () => now);
  }

  it('drops everything past the ceiling until the window ends', () => {
    now = 1_757_500_000_000;
    const sender = queue(3);

    for (let index = 0; index < 5; index += 1) sender.track(device, 'app_open');

    expect(sender.getStats().accepted).toBe(3);
    expect(sender.getStats().dropped).toBe(2);

    // Still inside the same window.
    now += 59_000;
    sender.track(device, 'app_open');
    expect(sender.getStats().accepted).toBe(3);

    // The window has ended: the count starts again.
    now += 2_000;
    sender.track(device, 'app_open');
    expect(sender.getStats().accepted).toBe(4);
  });

  it('is disabled by zero', () => {
    now = 1_757_500_000_000;
    const sender = queue(0);

    for (let index = 0; index < 200; index += 1) sender.track(device, 'app_open');

    expect(sender.getStats().accepted).toBe(200);
    expect(sender.getStats().dropped).toBe(0);
  });

  it('counts the ceiling before the event is looked at', () => {
    now = 1_757_500_000_000;
    const sender = queue(1);

    sender.track(device, 'app_open');
    // Refused by the window, not by the sanitizer — an unusable name would count as rejected.
    sender.track(device, '   ');

    const stats = sender.getStats();
    expect(stats.accepted).toBe(1);
    expect(stats.dropped).toBe(1);
    expect(stats.rejected).toBe(0);
  });
});
