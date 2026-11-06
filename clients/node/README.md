# Node client

This document is for developers who measure a Node.js backend with the collector (`POST {endpoint}/{source}`): a game server or an API called by a mobile app, or a website rendered by Node.
The rules every client follows are in the [contract](https://github.com/vapolia/analytics-clients/blob/main/clients/README.md).
For an Expo or React Native app, use [`@vapolia/analytics`](https://github.com/vapolia/analytics-clients/blob/main/clients/js/README.md) instead.

## Summary

```ts
import { Client, WebAnalytics } from '@vapolia/analytics-node';

const client = new Client({
  ingestionUrl: 'https://analytics.example.com/<sourceName>',
  token: process.env.ANALYTICS_SERVER_TOKEN,
});

// Server for an app: the app sends its install id and region in the request
client.for(installId, { country: 'FR' }).withClientAddress(req.ip).track('game_end', { result: 'win' });

// Website: the client sets the visitor cookie itself
const web = new WebAnalytics(client, { clientAddress: (req) => (req as express.Request).ip });
web.forNode(req, res).track('page_view', { page: 'home' });

// on shutdown
await client.close();
```

1. Create one `Client` per process, with a server token.
2. Server for an app: call `client.for(installId, device)` with what the app sent, then `track`.
3. Website: create one `WebAnalytics`, call `forNode(req, res)` or `forFetch(request, headers)` per request before the headers are sent, and show the consent banner while `consentAnswer` is undefined.
4. Declare every event name, property key and context key on the collector's source.
5. Call `close()` on shutdown. Without it, the buffered events are lost.

Node 20+. No runtime dependency. ESM and CommonJS.

## Install

```bash
npm i @vapolia/analytics-node
```

## Client

| | |
|---|---|
| `new Client(options)` | Starts the client. Throws a `TypeError` when `ingestionUrl` is not `https://baseUrl/sourceName`. |
| `track(installId, device, name, props?)` | Queues one event. Never throws, never blocks. |
| `trackContext(installId, device, context, name, props?)` | Same, with the batch context. |
| `for(installId, device?)` → `Session` | The installation and device bound once. |
| `Session.withContext(context)` | Batch context: what is true of the installation, a plan or a finished tutorial. |
| `Session.withTimeZone(minutes)` | The user's offset in minutes east of UTC, as the app reported it. Out of UTC−12:00..UTC+14:00, it is dropped. |
| `Session.withClientAddress(ip)` | The end client's IPv4 or IPv6 address. See [Client address](#client-address). |
| `Session.track(name, props?)` | Queues one event for the session. |
| `flush()` | Sends what is buffered and resolves once the collector answered. |
| `close()` | One last flush, then stops. Idempotent. Later events are ignored. |
| `discard(installId)` | Drops what is buffered for this installation. |
| `stats()` | `accepted`, `rejected`, `dropped`, `sent`, `requests`. |

`installId` is the UUID **the device generated**. A user id, a session id or a hash of either creates the join to a person that the design avoids, see [OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md).
`device.country` is the region setting the app reported, never derived from the client address.

`first_open` and `app_open` belong to the app. This client has no lifecycle and no stored identity for an app: it sends what the app tells it. An opposed installation stops calling the server.

Options: `ingestionUrl` (required), `token`, `isDebugBuild`, `excludedCountries`, `context` (a process-wide batch context, used when the call carries none), and `advanced`:

| `advanced` | Default | |
|---|---|---|
| `flushIntervalMs` | 30 000 | How long events are buffered. |
| `maxEventsPerWindow` | 0 | Disabled: one process speaks for every visitor. The mobile clients default to 30. |
| `rateWindowMs` | 60 000 | The window `maxEventsPerWindow` counts in. |
| `batchSize` | 100 | Events per request, at most 100. |
| `queueCapacity` | 4000 | Buffered events at most. Beyond it `track` drops. |
| `maxAttempts` | 3 | Attempts per request. |
| `requestTimeoutMs` | 10 000 | |
| `logger` | none | `{ warn(message), error(message) }`. |
| `onError` | none | `(error, reason, permanent)`, called on every loss. |

Events are grouped per (install id, country, context, client address). One group is one request of up to 100 events. The timer is `unref`'d and does not keep the process alive.

## Website

`WebAnalytics` identifies the visitor with the same cookies as the PHP and ASP.NET Core clients, so a site can move between them without losing its visitors.

| | |
|---|---|
| **Install id** | A first-party cookie (`_vau`, HttpOnly, Secure, SameSite=Lax) that the server sets once. It expires after 13 months and is never extended. |
| **Country** | The region of the first `Accept-Language` tag, never an IP geolocation. |
| **Opposition** | A second cookie, `_vau_off`: `1` opposed, `0` accepted, absent unanswered. |

```ts
const web = new WebAnalytics(client, {
  clientAddress: (req) => (req as express.Request).ip,      // behind a proxy, with `trust proxy` set
  context: (req) => ({ plan: planOf(req) }),
  timeZoneOffset: (req) => tzFromCookie(req),
});

// Express, Fastify (request.raw, reply.raw), node:http
app.get('/', (req, res) => {
  const visitor = web.forNode(req, res);
  visitor.track('page_view', { page: 'home' });
  res.render('home', { showBanner: visitor.consentAnswer === undefined });
});

// Next.js route handler, Hono: cookies go to the headers of the response you return
export async function GET(request: Request) {
  const headers = new Headers();
  web.forFetch(request, headers).track('page_view', { page: 'home' });
  return new Response(html, { headers });
}
```

A cookie can only be set before the headers are sent. A visitor seen for the first time after that is measured from the next request.

`Visitor`: `installId`, `country`, `consentAnswer` (true, false, undefined), `requiresPriorConsent`, `isOptedOut`, `setOptedOut(value)`, `track(name, props?)`.

### Consent

While the visitor has not answered, `isOptedOut` reads, in this order:

1. `requiresPriorConsentForLocale`, a function of the BCP-47 tag (undefined when the header has none).
2. `requiresPriorConsent`, a boolean for every visitor.
3. `localeRequiresPriorConsent(tag)`, the built-in list from [OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md).

**The built-in default must not be taken as a legal basis: the choice stays yours, and so does the liability for it.**

Until the visitor accepts under a prior-consent regime, no cookie is written and nothing is sent.
`setOptedOut(false)` writes `_vau_off=0`, which outranks the regime on the next requests.
`setOptedOut(true)` writes `_vau_off=1`, deletes `_vau` and drops the visitor's buffered events.

Web options: `requiresPriorConsent`, `requiresPriorConsentForLocale`, `cookieName` (`_vau`), `optOutCookieName` (`_vau_off`), `installIdLifetimeMs` and `optOutLifetimeMs` (390 days), `clientAddress`, `timeZoneOffset`, `context`.

### Relay for a JavaScript front end

A site whose pages render in the browser posts its events to the Node backend on the same domain, and the backend emits them. `web.relay(options)` serves the same three endpoints as the ASP.NET Core relay, matched on the last segment of the path:

| Endpoint | Body | Answer |
|---|---|---|
| `GET …/state` | | `{"requiresPriorConsent":bool,"consentAnswer":bool\|null}` |
| `POST …/consent` | `{"accepted":bool}` | 204, or 400 on an unreadable body |
| `POST …/events` | `[{"name":"…","props":{…}}]`, any content type | 204, always, no body |

```ts
const relay = web.relay({
  events: {
    session_start: [],
    content_viewed: ['content_type', 'content_id', 'duration_s'],
    route_shared: { keys: ['content_id'], with: { content_type: 'route' } },
  },
  // Per key, on every event that accepts it. Return the value to keep, or undefined to drop the prop.
  props: { duration_s: ({ value }) => (typeof value === 'number' ? Math.min(Math.max(value, 0), 86400) : undefined) },
});

app.use('/api/web/analytics', rateLimiter, relay.node);   // Express: an unknown path calls next()
export const POST = relay.fetch;                         // Next.js app/api/web/analytics/[endpoint]/route.ts
export const GET = relay.fetch;
```

Only the declared events and their declared keys are relayed. Events beyond `maxEventsPerRequest` (20) and bodies over `maxBodyBytes` (64 KB) are dropped. Put a rate limiter in front of the relay. The browser side is the snippet in the [ASP.NET Core README](https://github.com/vapolia/analytics-clients/blob/main/clients/dotnet/README.md#web-relay-for-a-javascript-front-end).

## Client address

Under a server token, the client sends the end client's IP address in `X-Analytics-Client-Address`, one address per request. The collector checks it against public blocklists, answers `204` and stores nothing when it is listed, and never stores nor logs the header. Without a token, the header is not sent.

- `Session.withClientAddress(ip)` sets it for a server called by an app.
- `forNode` reads `req.socket.remoteAddress`, which is the proxy's address behind a reverse proxy. Pass `clientAddress` in the web options to return the visitor's address instead, for example Express `req.ip` with `trust proxy` set. Do not read `X-Forwarded-For` yourself unless your proxy overwrites it.
- `forFetch` sends no address unless `clientAddress` is set.

An IPv4-mapped IPv6 address (`::ffff:a.b.c.d`) is sent in its IPv4 form. An invalid address is not sent.

## Failure behaviour

Measurement never fails a request, so nothing here surfaces an error to the caller:

- **Full queue**: the newest event is dropped. Counted in `stats().dropped`.
- **5xx, network error, 429**: retried up to `maxAttempts`, honouring `Retry-After`, then dropped. Backoff is capped at 5 s.
- **404 / 4xx**: permanent (unknown source, malformed payload), dropped without a retry and logged.
- **Events older than 7 days**: dropped before being sent.

With a server token, the collector throttles per token instead of per address.

## Tests

```bash
cd clients/node && pnpm install && pnpm test && pnpm typecheck && pnpm build
```

## Versioning

A GitHub release whose tag is `node-v<version>` publishes the package. The version must match `package.json`:

```bash
gh release create node-v1.0.0 --title "Node client 1.0.0" --notes "..."
```

A pre-release carries a semver suffix (`node-v1.1.0-beta.1`) and goes out under the `next` dist-tag. The publish is staged: approve it with `npm stage approve <stage-id>`.
