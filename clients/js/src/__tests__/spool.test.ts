import { describe, expect, it } from 'vitest';

import { Spool } from '../core/spool';
import type { Pending } from '../core/types';
import { MemoryStorage } from './support';

function pending(name: string): Pending {
  return {
    device: { country: 'FR' },
    installId: '11111111-0000-0000-0000-000011111111',
    event: { name, ts: 1_757_500_000_000, props: { result: 'win', moves: 34, ok: true } },
  };
}

describe('Spool', () => {
  it('round trips events, device context included', async () => {
    const spool = new Spool(new MemoryStorage(), 'testsource', 10);
    const items = [pending('app_open'), pending('game_end')];

    await spool.save(items);

    expect(await spool.load()).toEqual(items);
  });

  it('is read once', async () => {
    const storage = new MemoryStorage();
    const spool = new Spool(storage, 'testsource', 10);
    await spool.save([pending('app_open')]);

    expect(await spool.load()).toHaveLength(1);
    expect(storage.items.size).toBe(0);
    expect(await spool.load()).toHaveLength(0);
  });

  it('clears the key when there is nothing to save', async () => {
    const storage = new MemoryStorage();
    const spool = new Spool(storage, 'testsource', 10);
    await spool.save([pending('app_open')]);

    await spool.save([]);

    expect(storage.items.size).toBe(0);
  });

  it('yields nothing for a truncated payload', async () => {
    const storage = new MemoryStorage();
    const spool = new Spool(storage, 'testsource', 10);
    await spool.save([pending('app_open')]);
    const key = [...storage.items.keys()][0]!;
    storage.items.set(key, storage.items.get(key)!.slice(0, 20));

    expect(await spool.load()).toHaveLength(0);
  });

  it('keeps the oldest events when capped', async () => {
    const spool = new Spool(new MemoryStorage(), 'testsource', 2);

    await spool.save([pending('first'), pending('second'), pending('third')]);

    expect((await spool.load()).map((item) => item.event.name)).toEqual(['first', 'second']);
  });
});
