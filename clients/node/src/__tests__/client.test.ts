import { afterEach, describe, expect, it, vi } from 'vitest';
import { Client } from '../client.js';
import { collector, INSTALL, newClient, OTHER } from './support.js';

afterEach(() => {
  vi.useRealTimers();
});

describe('Client', () => {
  it('sends one batch carrying the contract fields and nothing else', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher);

    client.for(INSTALL, { country: 'fr' }).withContext({ plan: 'free' }).track('game_end', { result: 'win', moves: 34 });
    await client.flush();

    expect(requests).toHaveLength(1);
    const body = requests[0]!.body;
    expect(Object.keys(body).sort()).toEqual(['context', 'country', 'events', 'installId']);
    expect(body.installId).toBe(INSTALL);
    expect(body.country).toBe('FR');
    expect(body.context).toEqual({ plan: 'free' });
    expect(body.events).toEqual([{ name: 'game_end', ts: expect.any(String), props: { moves: 34, result: 'win' } }]);
    expect(requests[0]!.headers.get('Content-Type')).toBe('application/json');
    expect(client.stats()).toMatchObject({ accepted: 1, sent: 1, requests: 1 });
  });

  it('groups by install, country and context', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher);

    client.track(INSTALL, { country: 'FR' }, 'a');
    client.track(INSTALL, { country: 'FR' }, 'b');
    client.track(OTHER, { country: 'FR' }, 'c');
    client.track(INSTALL, { country: 'DE' }, 'd');
    client.trackContext(INSTALL, { country: 'FR' }, { plan: 'paid' }, 'e');
    await client.flush();

    expect(requests).toHaveLength(4);
    expect(requests[0]!.body.events).toHaveLength(2);
  });

  it('sends a batch as soon as it is full', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher, { advanced: { batchSize: 2 } });

    client.track(INSTALL, {}, 'a');
    client.track(INSTALL, {}, 'b');
    await client.flush();
    expect(requests).toHaveLength(1);
  });

  it('rejects an unusable install id, an empty name and an excluded country', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher, { excludedCountries: ['ru'] });

    client.track('user-42', {}, 'a');
    client.track('00000000-0000-0000-0000-000000000000', {}, 'a');
    client.track(INSTALL, {}, '  ');
    client.track(INSTALL, { country: 'RU' }, 'a');
    await client.flush();

    expect(requests).toHaveLength(0);
    expect(client.stats().rejected).toBe(4);
  });

  it('drops non-scalar props and cleans keys like values', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher);

    client.track(INSTALL, {}, 'a', { ok: 'x'.repeat(100), nested: { a: 1 }, nan: Number.NaN, ' key\u0000 ': true });
    await client.flush();

    const event = (requests[0]!.body.events as Array<{ props: Record<string, unknown> }>)[0]!;
    expect(event.props).toEqual({ key: true, ok: 'x'.repeat(64) });
  });

  it('retries a 5xx, then gives up after maxAttempts', async () => {
    const { requests, fetcher } = collector([500, 500, 500]);
    const client = newClient(fetcher, { advanced: { maxAttempts: 3 } });

    client.track(INSTALL, {}, 'a');
    await client.flush();

    expect(requests).toHaveLength(3);
    expect(client.stats()).toMatchObject({ dropped: 1, sent: 0, requests: 3 });
  });

  it('honours Retry-After on a 429', async () => {
    vi.useFakeTimers();
    const { requests, fetcher } = collector([{ status: 429, retryAfter: '2' }]);
    const client = newClient(fetcher);

    client.track(INSTALL, {}, 'a');
    const flushed = client.flush();
    await vi.advanceTimersByTimeAsync(1_900);
    expect(requests).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(200);
    await flushed;

    expect(requests).toHaveLength(2);
    expect(client.stats().sent).toBe(1);
  });

  it('does not retry an unknown source', async () => {
    const { requests, fetcher } = collector([404]);
    const client = newClient(fetcher);

    client.track(INSTALL, {}, 'a');
    await client.flush();

    expect(requests).toHaveLength(1);
    expect(client.stats().dropped).toBe(1);
  });

  it('drops events older than 7 days', async () => {
    const { requests, fetcher } = collector();
    let now = Date.parse('2026-09-01T00:00:00Z');
    const client = newClient(fetcher, { advanced: { now: () => now } });

    client.track(INSTALL, {}, 'a');
    now += 8 * 24 * 60 * 60 * 1000;
    await client.flush();

    expect(requests).toHaveLength(0);
    expect(client.stats().dropped).toBe(1);
  });

  it('drops instead of blocking on a full queue', async () => {
    const { fetcher } = collector();
    const client = newClient(fetcher, { advanced: { queueCapacity: 2 } });

    client.track(INSTALL, {}, 'a');
    client.track(INSTALL, {}, 'b');
    client.track(INSTALL, {}, 'c');

    expect(client.stats()).toMatchObject({ accepted: 2, dropped: 1 });
  });

  it('flushes on close, is idempotent, and ignores what is tracked afterwards', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher);

    client.track(INSTALL, {}, 'a');
    await Promise.all([client.close(), client.close()]);
    client.track(INSTALL, {}, 'b');
    await client.flush();

    expect(requests).toHaveLength(1);
  });

  it('sends the time zone of the session and drops one out of range', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher);

    client.for(INSTALL).withTimeZone(120).track('a');
    client.for(INSTALL).withTimeZone(15 * 60).track('b');
    await client.flush();

    const events = requests[0]!.body.events as Array<{ name: string; tz?: number }>;
    expect(events.map((e) => e.tz)).toEqual([120, undefined]);
  });

  it('measures nothing in a debug build', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher, { isDebugBuild: true });

    client.track(INSTALL, {}, 'a');
    await client.flush();
    expect(requests).toHaveLength(0);
  });

  it('refuses an ingestion URL without a source', () => {
    expect(() => new Client({ ingestionUrl: 'https://analytics.example.com' })).toThrow(TypeError);
    expect(() => new Client({ ingestionUrl: 'analytics.example.com/source' })).toThrow(TypeError);
  });

  it('discards what is buffered for one installation', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher);

    client.track(INSTALL, {}, 'a');
    client.track(OTHER, {}, 'b');
    client.discard(INSTALL);
    await client.flush();

    expect(requests.map((r) => r.body.installId)).toEqual([OTHER]);
  });
});

describe('X-Analytics-Client-Address', () => {
  it('sends one request per address, each with its header', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher, { token: 'server-token' });

    client.for(INSTALL).withClientAddress('203.0.113.7').track('a');
    client.for(INSTALL).withClientAddress('2001:db8::1').track('b');
    client.for(INSTALL).withClientAddress('203.0.113.7').track('c');
    await client.flush();

    expect(requests).toHaveLength(2);
    expect(requests.map((r) => r.headers.get('X-Analytics-Client-Address'))).toEqual(['203.0.113.7', '2001:db8::1']);
    expect(requests[0]!.body.events).toHaveLength(2);
    expect(requests[0]!.headers.get('Authorization')).toBe('Bearer server-token');
  });

  it('normalises an IPv4-mapped IPv6 address and drops an invalid one', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher, { token: 'server-token' });

    client.for(INSTALL).withClientAddress('::FFFF:198.51.100.4').track('a');
    client.for(OTHER).withClientAddress('not-an-ip').track('b');
    await client.flush();

    expect(requests.map((r) => r.headers.get('X-Analytics-Client-Address'))).toEqual(['198.51.100.4', null]);
  });

  it('is not sent without a token', async () => {
    const { requests, fetcher } = collector();
    const client = newClient(fetcher);

    client.for(INSTALL).withClientAddress('203.0.113.7').track('a');
    await client.flush();

    expect(requests[0]!.headers.get('X-Analytics-Client-Address')).toBeNull();
  });
});
