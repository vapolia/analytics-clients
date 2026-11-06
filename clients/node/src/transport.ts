export type SendResult =
  | { kind: 'ok' }
  /** Worth sending again: a network error, a 5xx, or a 429 with its `Retry-After`. */
  | { kind: 'retry'; afterMs: number; reason: string }
  /** A retry cannot fix it: unknown source, malformed payload. */
  | { kind: 'permanent'; reason: string };

export const CLIENT_ADDRESS_HEADER = 'X-Analytics-Client-Address';

/** One request to `POST {endpoint}/{source}`. */
export async function post(
  fetcher: typeof fetch,
  url: string,
  body: string,
  timeoutMs: number,
  token: string | undefined,
  clientAddress: string | undefined
): Promise<SendResult> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);

  try {
    const headers: Record<string, string> = { 'Content-Type': 'application/json' };
    if (token) {
      headers.Authorization = `Bearer ${token}`;
      // The collector reads the address only under a server token, so it is not sent without one.
      if (clientAddress) headers[CLIENT_ADDRESS_HEADER] = clientAddress;
    }

    const response = await fetcher(url, { method: 'POST', headers, body, signal: controller.signal });
    // The collector answers 204 with no body. Reading it out lets the connection be reused.
    await response.arrayBuffer().catch(() => undefined);

    if (response.ok) return { kind: 'ok' };

    if (response.status === 429) {
      const header = response.headers.get('Retry-After');
      const seconds = header ? Number.parseInt(header.trim(), 10) : Number.NaN;
      const afterMs = Number.isFinite(seconds) && seconds > 0 ? seconds * 1000 : 0;
      return { kind: 'retry', afterMs, reason: 'rate limited (429)' };
    }

    // 404 means this source is not configured there, 400 that the payload is not accepted.
    if (response.status === 404) return { kind: 'permanent', reason: 'unknown source (404)' };
    if (response.status < 500) return { kind: 'permanent', reason: `refused with ${response.status}` };

    return { kind: 'retry', afterMs: 0, reason: `collector answered ${response.status}` };
  } catch (error) {
    return { kind: 'retry', afterMs: 0, reason: `network error: ${String(error)}` };
  } finally {
    clearTimeout(timeout);
  }
}
