import { describe, expect, it } from 'vitest';

import { Identity, randomUuid } from '../core/identity';
import { installId } from '../core/clean';
import { MemoryStorage } from './support';

const DAY_MS = 24 * 60 * 60 * 1000;

describe('randomUuid', () => {
  it('produces an id the collector accepts', () => {
    expect(installId(randomUuid())).toBeDefined();
    expect(randomUuid()).not.toBe(randomUuid());
  });
});

describe('Identity', () => {
  it('issues an id on the first launch and keeps it on the next', async () => {
    const storage = new MemoryStorage();
    const first = new Identity(storage);
    await first.load();
    const id = first.current();

    expect(installId(id)).toBeDefined();

    const second = new Identity(storage);
    await second.load();
    expect(second.current()).toBe(id);
  });

  it('rotates the id once it reaches its ceiling', async () => {
    const storage = new MemoryStorage();
    let now = Date.now();
    const first = new Identity(storage, () => now);
    await first.load();
    const id = first.current();

    now += 391 * DAY_MS;
    const later = new Identity(storage, () => now);
    await later.load();

    expect(later.current()).not.toBe(id);
    expect(installId(later.current())).toBeDefined();
  });

  it('reissues rather than extends when the clock moved backwards', async () => {
    const storage = new MemoryStorage();
    let now = Date.now();
    const first = new Identity(storage, () => now);
    await first.load();
    const id = first.current();

    now -= 10 * DAY_MS;
    const rewound = new Identity(storage, () => now);
    await rewound.load();

    expect(rewound.current()).not.toBe(id);
  });

  it('tracks first_open once, across a rotation', async () => {
    const storage = new MemoryStorage();
    const first = new Identity(storage);
    await first.load();

    expect(first.isFirstOpenPending()).toBe(true);
    await first.markFirstOpenSent();

    let now = Date.now() + 391 * DAY_MS;
    const later = new Identity(storage, () => now);
    await later.load();
    now += 1;

    expect(later.isFirstOpenPending()).toBe(false);
  });

  it('forgets the id when opting out, and issues a new one when opting back in', async () => {
    const storage = new MemoryStorage();
    const identity = new Identity(storage);
    await identity.load();
    const id = identity.current();

    await identity.setOptedOut(true);
    expect(identity.current()).toBeUndefined();
    expect(identity.isOptedOut()).toBe(true);

    await identity.setOptedOut(false);
    expect(identity.current()).not.toBe(id);
    expect(installId(identity.current())).toBeDefined();
  });

  it('stays opted out across launches', async () => {
    const storage = new MemoryStorage();
    const first = new Identity(storage);
    await first.load();
    await first.setOptedOut(true);

    const second = new Identity(storage);
    await second.load();

    expect(second.isOptedOut()).toBe(true);
    expect(second.current()).toBeUndefined();
  });

  it('writes nothing while a consent regime has not been answered', async () => {
    const storage = new MemoryStorage();
    const identity = new Identity(storage, undefined, undefined, undefined, undefined, true);
    await identity.load();

    expect(identity.isOptedOut()).toBe(true);
    expect(identity.current()).toBeUndefined();
    // No identifier may be written on the device before the answer.
    expect(storage.items.size).toBe(0);
  });

  it('collects once consent is given, and keeps collecting on the next launch', async () => {
    const storage = new MemoryStorage();
    const first = new Identity(storage, undefined, undefined, undefined, undefined, true);
    await first.load();
    await first.setOptedOut(false);

    expect(first.isOptedOut()).toBe(false);
    expect(installId(first.current())).toBeDefined();

    // The stored acceptance outranks the default, so the popup's answer is not asked again.
    const second = new Identity(storage, undefined, undefined, undefined, undefined, true);
    await second.load();
    expect(second.isOptedOut()).toBe(false);
    expect(second.current()).toBe(first.current());
  });

  it('reports the consent answer: null until asked, then the last button pressed', async () => {
    const storage = new MemoryStorage();
    const first = new Identity(storage, undefined, undefined, undefined, undefined, true);
    await first.load();
    expect(first.consentAnswer()).toBeNull();

    await first.setOptedOut(false);
    expect(first.consentAnswer()).toBe(true);

    const second = new Identity(storage, undefined, undefined, undefined, undefined, true);
    await second.load();
    expect(second.consentAnswer()).toBe(true);

    await second.setOptedOut(true);
    const third = new Identity(storage);
    await third.load();
    expect(third.consentAnswer()).toBe(false);
  });
});
