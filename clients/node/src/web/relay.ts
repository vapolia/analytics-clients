import type { IncomingMessage, ServerResponse } from 'node:http';
import type { PropValue } from '../types.js';
import type { Visitor, WebAnalytics, WebRequest } from './visitor.js';

/** One prop of a relayed event, as the browser sent it. */
export interface RelayProp {
  event: string;
  key: string;
  value: PropValue;
  /** All props of the event before validation, fixed props included. */
  props: Readonly<Record<string, PropValue>>;
  request: WebRequest;
}

/** Returns the value to keep, or undefined to drop the prop. The event itself is kept. */
export type RelayPropValidator = (prop: RelayProp) => PropValue | undefined | Promise<PropValue | undefined>;

export interface RelayEvent {
  /** The props the browser may send. */
  keys: readonly string[];
  /** Props the server sets itself, whatever the browser sent. Not validated. */
  with?: Readonly<Record<string, PropValue>>;
}

export interface RelayOptions {
  /** The accepted events, by name: the keys the browser may send, or a `RelayEvent`. */
  events: Readonly<Record<string, readonly string[] | RelayEvent>>;
  /** Validators per prop key, on every event that accepts the key. A key without one is relayed as sent. */
  props?: Readonly<Record<string, RelayPropValidator>>;
  /** Events beyond this count in one request are dropped. Default 20. */
  maxEventsPerRequest?: number;
  /** A body larger than this is dropped whole. Default 64 KB. */
  maxBodyBytes?: number;
}

type Endpoint = 'state' | 'consent' | 'events';

/**
 * `GET state`, `POST consent` and `POST events`, matched on the last segment of the path. Same
 * contract as the ASP.NET Core relay.
 */
export class Relay {
  private readonly events: Map<string, RelayEvent>;
  private readonly maxEvents: number;
  private readonly maxBytes: number;

  constructor(
    private readonly web: WebAnalytics,
    private readonly options: RelayOptions
  ) {
    this.events = new Map(
      Object.entries(options.events).map(([name, value]) => [name, isKeyList(value) ? { keys: value } : value])
    );
    this.maxEvents = options.maxEventsPerRequest ?? 20;
    this.maxBytes = options.maxBodyBytes ?? 64 * 1024;
  }

  /** A node:http handler, also an Express or Connect middleware: an unknown path calls `next`. */
  readonly node = async (req: IncomingMessage, res: ServerResponse, next?: (error?: unknown) => void): Promise<void> => {
    const endpoint = match(req.method, req.url ?? '/');
    if (endpoint === undefined) {
      if (next) return next();
      res.statusCode = 404;
      res.end();
      return;
    }

    const visitor = this.web.forNode(req, res);
    if (endpoint === 'state') {
      res.statusCode = 200;
      res.setHeader('Content-Type', 'application/json');
      res.setHeader('Cache-Control', 'no-store');
      res.end(state(visitor));
      return;
    }

    const body = await readNode(req, this.maxBytes);
    res.statusCode = endpoint === 'consent' ? this.consent(visitor, body) : await this.track(visitor, body, req);
    res.end();
  };

  /** A Fetch API handler, for a Next.js route handler or Hono. An unknown path answers 404. */
  readonly fetch = async (request: Request): Promise<Response> => {
    const endpoint = match(request.method, new URL(request.url).pathname);
    if (endpoint === undefined) return new Response(null, { status: 404 });

    const headers = new Headers();
    const visitor = this.web.forFetch(request, headers);
    if (endpoint === 'state') {
      headers.set('Content-Type', 'application/json');
      headers.set('Cache-Control', 'no-store');
      return new Response(state(visitor), { status: 200, headers });
    }

    const body = await readFetch(request, this.maxBytes);
    const status = endpoint === 'consent' ? this.consent(visitor, body) : await this.track(visitor, body, request);
    return new Response(null, { status, headers });
  };

  private consent(visitor: Visitor, body: unknown): number {
    if (!isObject(body) || typeof body.accepted !== 'boolean') return 400;
    visitor.setOptedOut(!body.accepted);
    return 204;
  }

  /** Always 204: a beacon reads no answer. */
  private async track(visitor: Visitor, body: unknown, request: WebRequest): Promise<number> {
    const items = Array.isArray(body) ? body.slice(0, this.maxEvents) : isObject(body) ? [body] : [];

    for (const item of items) {
      if (!isObject(item) || typeof item.name !== 'string') continue;
      const relayEvent = this.events.get(item.name);
      if (relayEvent === undefined) continue;

      const sent: Record<string, PropValue> = {};
      if (isObject(item.props)) {
        for (const [key, value] of Object.entries(item.props)) {
          if (relayEvent.keys.includes(key) && isScalar(value)) sent[key] = value;
        }
      }
      const fixed = relayEvent.with ?? {};
      Object.assign(sent, fixed);

      const props: Record<string, PropValue> = {};
      for (const [key, value] of Object.entries(sent)) {
        const validate = this.options.props?.[key];
        if (key in fixed || validate === undefined) {
          props[key] = value;
          continue;
        }

        try {
          const kept = await validate({ event: item.name, key, value, props: sent, request });
          if (kept !== undefined) props[key] = kept;
        } catch {
          // A failing validator drops the prop, never the request.
        }
      }

      visitor.track(item.name, props);
    }

    return 204;
  }
}

function isKeyList(value: readonly string[] | RelayEvent): value is readonly string[] {
  return Array.isArray(value);
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isScalar(value: unknown): value is PropValue {
  return typeof value === 'string' || typeof value === 'boolean' || (typeof value === 'number' && Number.isFinite(value));
}

function match(method: string | undefined, url: string): Endpoint | undefined {
  const path = url.split('?')[0]?.replace(/\/+$/, '') ?? '';
  const last = path.slice(path.lastIndexOf('/') + 1);
  if (last === 'state' && method === 'GET') return 'state';
  if (last === 'consent' && method === 'POST') return 'consent';
  if (last === 'events' && method === 'POST') return 'events';
  return undefined;
}

function state(visitor: Visitor): string {
  return JSON.stringify({ requiresPriorConsent: visitor.requiresPriorConsent, consentAnswer: visitor.consentAnswer ?? null });
}

function parse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}

/** The body as JSON, or undefined when it is too large or unreadable. Any content type is read as JSON. */
async function readNode(req: IncomingMessage, maxBytes: number): Promise<unknown> {
  // A body parser that ran first, such as express.json(), already consumed the stream.
  const parsed = (req as { body?: unknown }).body;
  if (parsed !== undefined) return typeof parsed === 'string' ? parse(parsed) : parsed;

  if (Number(req.headers['content-length'] ?? 0) > maxBytes) return undefined;

  const chunks: Buffer[] = [];
  let size = 0;
  try {
    for await (const chunk of req) {
      const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk as string);
      size += buffer.length;
      if (size > maxBytes) return undefined;
      chunks.push(buffer);
    }
  } catch {
    return undefined;
  }
  return parse(Buffer.concat(chunks).toString('utf8'));
}

async function readFetch(request: Request, maxBytes: number): Promise<unknown> {
  if (Number(request.headers.get('content-length') ?? 0) > maxBytes) return undefined;
  if (request.body === null) return undefined;

  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > maxBytes) {
        await reader.cancel().catch(() => undefined);
        return undefined;
      }
      chunks.push(value);
    }
  } catch {
    return undefined;
  }
  return parse(Buffer.concat(chunks).toString('utf8'));
}
