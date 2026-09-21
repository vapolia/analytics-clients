import { describe, expect, it } from 'vitest';

import { batchKey, encodeBatch } from '../core/batch';
import type { AnalyticsEvent } from '../core/types';

const installId = '11111111-0000-0000-0000-000011111111';

function event(name: string, ts: number, props: AnalyticsEvent['props'] = {}): AnalyticsEvent {
  return { name, ts, props };
}

describe('encodeBatch', () => {
  it('encodes the shape the collector expects', () => {
    const json = encodeBatch(
      installId,
      { country: 'FR' },
      [event('app_open', 1_757_500_000_000), event('game_end', 1_757_500_001_000, { result: 'win', moves: 34 })],
      { plan: 'free' }
    );

    const parsed = JSON.parse(json);
    expect(parsed.installId).toBe(installId);
    expect(parsed.country).toBe('FR');
    expect(parsed.context).toEqual({ plan: 'free' });
    expect(parsed.events).toHaveLength(2);
    expect(parsed.events[1].props).toEqual({ result: 'win', moves: 34 });
    // Nothing is sent for a field the device did not fill in.
    expect('osVersion' in parsed).toBe(false);
    expect('props' in parsed.events[0]).toBe(false);
  });

  it('writes timestamps as iso 8601 in utc', () => {
    const json = encodeBatch(installId, {}, [event('app_open', 0)]);

    expect(JSON.parse(json).events[0].ts).toBe('1970-01-01T00:00:00.000Z');
  });

  it('orders events by timestamp', () => {
    const json = encodeBatch(installId, {}, [event('second', 200), event('first', 100)]);

    expect(JSON.parse(json).events.map((item: { name: string }) => item.name)).toEqual([
      'first',
      'second',
    ]);
  });
});

describe('the contract', () => {
  /**
   * The contract's closed list. This is the test that keeps the four clients from drifting apart
   * again: the platform, the build, the OS version, the device class, the store and the locale come
   * from the `Authorization` token, never the body.
   */
  it('carries its fields and nothing else', () => {
    const json = encodeBatch(installId, { country: 'FR' }, [event('app_open', 0, { a: 1 })], {
      plan: 'free',
    });

    const parsed = JSON.parse(json);
    expect(Object.keys(parsed).sort()).toEqual(['context', 'country', 'events', 'installId']);
    expect(Object.keys(parsed.events[0]).sort()).toEqual(['name', 'props', 'ts']);
  });

  it('leaves out a country it does not know', () => {
    const parsed = JSON.parse(encodeBatch(installId, {}, [event('app_open', 0)]));
    expect(Object.keys(parsed).sort()).toEqual(['events', 'installId']);
  });
});

describe('batchKey', () => {
  it('groups the same device and separates a different one', () => {
    expect(batchKey({ country: 'FR' })).toBe(batchKey({ country: 'FR' }));
    expect(batchKey({})).not.toBe(batchKey({ country: 'FR' }));
  });

  it('separates two contexts, whatever the key order', () => {
    const device = {};

    expect(batchKey(device, { plan: 'free', seen: true })).toBe(
      batchKey(device, { seen: true, plan: 'free' })
    );
    // An event carries the state it was produced under: two contexts cannot share a batch.
    expect(batchKey(device, { plan: 'free' })).not.toBe(batchKey(device, { plan: 'premium' }));
  });
});
