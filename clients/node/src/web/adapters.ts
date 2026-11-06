import type { IncomingMessage, ServerResponse } from 'node:http';

/** What the web layer reads from a request and writes to its response. */
export interface RequestAdapter {
  cookie(name: string): string | undefined;
  /** Sets a first-party cookie, HttpOnly, Secure, SameSite=Lax, path `/`. A zero lifetime deletes it. */
  setCookie(name: string, value: string, maxAgeSeconds: number): void;
  header(name: string): string | undefined;
  /** False once the response headers are sent: a cookie can no longer be set. */
  canSetCookies(): boolean;
  /** The address of the peer, before any proxy configuration. */
  remoteAddress(): string | undefined;
}

export function parseCookies(header: string | undefined): Map<string, string> {
  const cookies = new Map<string, string>();
  for (const pair of (header ?? '').split(';')) {
    const at = pair.indexOf('=');
    if (at <= 0) continue;

    const name = pair.slice(0, at).trim();
    if (cookies.has(name)) continue;
    let value = pair.slice(at + 1).trim();
    if (value.startsWith('"') && value.endsWith('"')) value = value.slice(1, -1);
    try {
      cookies.set(name, decodeURIComponent(value));
    } catch {
      cookies.set(name, value);
    }
  }
  return cookies;
}

export function serializeCookie(name: string, value: string, maxAgeSeconds: number): string {
  const expires = maxAgeSeconds > 0 ? new Date(Date.now() + maxAgeSeconds * 1000) : new Date(0);
  return [
    `${name}=${encodeURIComponent(value)}`,
    `Max-Age=${Math.max(0, Math.floor(maxAgeSeconds))}`,
    `Expires=${expires.toUTCString()}`,
    'Path=/',
    'Secure',
    'HttpOnly',
    'SameSite=Lax',
  ].join('; ');
}

/** A node:http request and its response. Express, Fastify (`request.raw`, `reply.raw`) and Koa pass these. */
export function fromNode(req: IncomingMessage, res: ServerResponse): RequestAdapter {
  let cookies: Map<string, string> | undefined;
  return {
    cookie: (name) => (cookies ??= parseCookies(req.headers.cookie)).get(name),
    setCookie: (name, value, maxAgeSeconds) => {
      const previous = res.getHeader('Set-Cookie');
      const list = previous === undefined ? [] : Array.isArray(previous) ? previous : [String(previous)];
      res.setHeader('Set-Cookie', [...list, serializeCookie(name, value, maxAgeSeconds)]);
    },
    header: (name) => {
      const value = req.headers[name.toLowerCase()];
      return Array.isArray(value) ? value.join(', ') : value;
    },
    canSetCookies: () => !res.headersSent,
    remoteAddress: () => req.socket?.remoteAddress,
  };
}

/**
 * A Fetch API request, and the headers of the response that will answer it (a Next.js route
 * handler, Hono, Deno-style servers). The Fetch API carries no peer address.
 */
export function fromFetch(request: Request, responseHeaders: Headers): RequestAdapter {
  let cookies: Map<string, string> | undefined;
  return {
    cookie: (name) => (cookies ??= parseCookies(request.headers.get('cookie') ?? undefined)).get(name),
    setCookie: (name, value, maxAgeSeconds) =>
      responseHeaders.append('Set-Cookie', serializeCookie(name, value, maxAgeSeconds)),
    header: (name) => request.headers.get(name) ?? undefined,
    canSetCookies: () => true,
    remoteAddress: () => undefined,
  };
}
