export type SendResult =
  | { kind: 'ok' }
  /** Worth sending again: a network error, a 5xx, or a 429 with its `Retry-After`. */
  | { kind: 'retry'; afterMs: number; reason: string }
  /** A retry cannot fix it: unknown source, malformed payload. */
  | { kind: 'permanent'; reason: string };

/** What the sender needs from the network. An interface so the send path is testable without one. */
export interface Poster {
  post(body: string): Promise<SendResult>;
}

/**
 * One request to `POST {endpoint}/{source}`, on `fetch` — which every React Native runtime provides,
 * and which never blocks the JS thread.
 */
export class FetchPoster implements Poster {
  constructor(
    private readonly url: string,
    private readonly timeoutMs: number
  ) {}

  async post(body: string): Promise<SendResult> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), this.timeoutMs);

    try {
      const response = await fetch(this.url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body,
        signal: controller.signal,
      });

      if (response.ok) return { kind: 'ok' };

      if (response.status === 429) {
        const header = response.headers.get('Retry-After');
        const seconds = header ? Number.parseInt(header.trim(), 10) : Number.NaN;
        const afterMs = Number.isFinite(seconds) && seconds > 0 ? seconds * 1000 : 0;
        return { kind: 'retry', afterMs, reason: 'rate limited (429)' };
      }

      // 404 means this source is not configured there, 400 that the payload is not accepted.
      if (response.status === 404) return { kind: 'permanent', reason: 'unknown source (404)' };
      if (response.status < 500) {
        return { kind: 'permanent', reason: `refused with ${response.status}` };
      }

      return { kind: 'retry', afterMs: 0, reason: `collector answered ${response.status}` };
    } catch (error) {
      return { kind: 'retry', afterMs: 0, reason: `network error: ${String(error)}` };
    } finally {
      clearTimeout(timeout);
    }
  }
}
