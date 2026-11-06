import { Client } from '../client.js';
import type { AnalyticsOptions } from '../types.js';

export const INSTALL = '11111111-0000-0000-0000-000011111111';
export const OTHER = '22222222-0000-0000-0000-000022222222';

export interface Recorded {
  body: Record<string, unknown>;
  raw: string;
  headers: Headers;
}

/** A fake collector: records every request and answers the queued statuses, then 204. */
export function collector(statuses: Array<number | { status: number; retryAfter?: string }> = []) {
  const requests: Recorded[] = [];
  const fetcher = (async (_url: string | URL | Request, init?: RequestInit) => {
    const raw = String(init?.body ?? '');
    requests.push({ body: JSON.parse(raw) as Record<string, unknown>, raw, headers: new Headers(init?.headers) });
    const next = statuses.shift() ?? 204;
    const { status, retryAfter } = typeof next === 'number' ? { status: next, retryAfter: undefined } : next;
    const headers = new Headers();
    if (retryAfter !== undefined) headers.set('Retry-After', retryAfter);
    return new Response(status === 204 ? null : '', { status, headers });
  }) as typeof fetch;
  return { requests, fetcher };
}

export function newClient(fetcher: typeof fetch, options: Partial<AnalyticsOptions> = {}): Client {
  return new Client({
    ingestionUrl: 'https://analytics.example.com/testsource',
    ...options,
    advanced: { flushIntervalMs: 3_600_000, fetch: fetcher, ...options.advanced },
  });
}
