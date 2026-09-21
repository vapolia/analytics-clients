# Clients

One subdirectory per client language. Each one speaks the same endpoint —
`POST {endpoint}/{source}` — and is independent: no shared build, no shared package.

| Path | Target |
|---|---|
| [`go/`](go/README.md) | Go 1.24+, stdlib only. Server-side callers; |
| [`kotlin/`](kotlin/README.md) | Android, minSdk 24, no dependency. `com.vapolia.analytics:analytics`. Holds the install id, the device context and the lifecycle, which a server-side client has none of. |
| [`swift/`](swift/README.md) | iOS 15+ / macCatalyst, no dependency. SwiftPM (`Package.swift` at the repository root, as SwiftPM requires). Same responsibilities as the Kotlin one, plus its own `PrivacyInfo.xcprivacy`. |
| [`js/`](js/README.md) | Expo / React Native, TypeScript. `@vapolia/analytics` on npm. AsyncStorage required, the `expo-*` packages optional. Spools continuously, because a JS runtime has no `beginBackgroundTask`. |
| [`dotnet/`](dotnet/README.md) | .NET 10: MAUI/Android/iOS/Windows apps (`Vapolia.Analytics.Client`) and Blazor/ASP.NET Core servers (`Vapolia.Analytics.Client.AspNetCore`), same `AddAnalytics` on both. The only client that measures a **website without any script**: server-side emission, install id in a first-party cookie, country from `Accept-Language`. |

**Integrating one of these?** [OBLIGATIONS.md](OBLIGATIONS.md) is the short list of what a library
cannot carry for you — informing people, offering a refusal, renewing the identifier, declaring
accurately in the stores. The exemption waives consent, not information.

A client is an app embedding one of these libraries. A client not using the SDK implements the same
contract by hand.

## TL;DR — the contract every client implements

1. One request per (install id, device, batch context). 
   Global properties sit on the batch, events in `events[]`; at most 100 events per request. 
   That ceiling is the collector's — a source may raise it — and the collector cuts what a client sends past it, silently.
2. Never block the caller, never surface an error.
   The collector answers `204` even when its own write failed, so a client has nothing to act on. Queue, batch, retry a few times, then drop.
3. Whitelisted names only. 
   Event names, property keys and batch-context keys come from the source's three lists in the collector's configuration.
   Anything else is dropped server-side without a word — that silence is deliberate, not a bug to work around.
4. **One key, once.** A body carrying the same property twice is refused whole — the collector will not
   pick between two readings of one payload. No client can produce this by serialising a map; a client
   assembling JSON by hand can.
5. **Scalars only in `props` and `context`**: string (≤64 chars), finite number, bool. Max 12 each.
   `props` describes one event; `context` describes the installation for the whole batch, so a change of context starts another batch.
   The body's top level is a **closed list** — `installId`, `country`, `context`, `events`, and nothing
   else. Every client has a test asserting exactly that; a client sending a sixth key is a bug in that
   client, not a variation.
6. **`installId` is a device-generated UUID, rotated by the device**, never an account id or anything derived from one. 
   `country` is the device's region setting, never a geolocation of the IP.
7. **No client emits an event of its own.** `first_open` and `app_open` are named and placed by the
   app: a name the client invented is a name the whitelist may not carry, and it would be dropped in
   silence. Each client exposes what the app needs for it — a first-run flag, and the lifecycle it
   already watches in order to flush.
8. **Excluded countries are refused client-side too.** The deployment lists them (`excludedCountries`, e.g. KR for a
   13+ app: PIPA, guardian consent under 14) — globally, or on the source when it needs a list of its own; the app passes
   whichever applies to its client, which then sends nothing from there. The collector enforces it as well; the
   duplication is intentional. No client hardcodes a country.
9. **One token, one header, for both roles.** Every credential — a server's own key or a build's own token — is sent
   as `Authorization: Bearer <token>`. The collector tells the two apart from the token's own claims, not from how it
   arrived, and either one names the platform and the build: no client sends those in the body. A missing, expired or
   revoked build token is a `401`: that build is no longer measured. Presenting nothing is the regime for an
   untrusted client, limited by address.

## Payload

```json
{
  "installId": "11111111-0000-0000-0000-000011111111",
  "country": "FR",
  "context": { "plan": "premium", "tutorial_done": true, "install_age": "8-30" },
  "events": [
    { "name": "app_open", "ts": "2026-09-09T10:00:00Z", "tz": 120 },
    { "name": "game_end", "ts": "2026-09-09T10:12:31Z", "props": { "result": "win", "moves": 34 } }
  ]
}
```

Every field but `installId` and `events` is optional — and `installId` too for a server sending under its server token: its events then belong to no installation (`install_id` NULL), and a batch without one and without a token is dropped. The platform, the build, the OS version, the device class, the store **and the locale** come from the `Authorization` token alone, never the body: a build that names itself could name anything, and a token cannot. A deployment whose builds carry no token does not measure those axes at all — that is the price of the guarantee, and it is deliberate. `context` keys are the source's own: the
collector stores it as one jsonb column and knows nothing about what is in it. `country` stays top-level because it is
the axis of the source's `excludedCountries` filter — it is the one thing the body still says about the device.
Anything a caller wants to add about its own client — a browser and its version, for a server relay — goes in `context`
or in an event's `props`, under a whitelisted key.
`ts` defaults to the server's clock, is dropped when more than 5 minutes ahead (never recalibrated) or when more
than 7 days old. `tz` is the UTC offset in minutes east of UTC when the event happened (UTC+2 is 120), sent apart from `ts`, which stays UTC; absent means unknown, not UTC. The collector stamps every batch with its own `received_at`.

## Sending

Events queue in memory and leave in the background, grouped by install id, device and batch context.
A group is sent when it reaches the batch size, when the flush interval elapses (30 s everywhere), or
when the app asks for a flush. Losses are counted, never reported: each client exposes counters.

| Situation | What a client does |
|---|---|
| Queue full | Drops the newest event, so the loss stays bounded and the caller unblocked. |
| Own rate window saturated | Drops until the window ends, rather than getting the whole address throttled — which behind a carrier NAT hits every other installation. `maxEventsPerWindow` (30) counts in a fixed `rateWindow` (60 s), and zero disables it — which is the default server-side, where one process speaks for every visitor. |
| `429`, `5xx`, network | Retried with backoff, a few attempts, then dropped — kept for the next flush on mobile. |
| `404` and other `4xx` | Permanent, dropped at once. |

The mobile and js clients write what is unsent to disk, so a restart resumes rather than loses. The
js one spools a few hundred milliseconds after each event rather than on background alone: a JS
runtime has no equivalent of `beginBackgroundTask`.

Every client names these the same way, because the .NET client is the reference the others follow:
`ingestionUrl` (`https://baseUrl/sourceName`), `token`, `enabled`, `excludedCountries`, `context`,
`seedInstallId`, and — under `advanced` — `flushInterval` (30 s), `maxEventsPerWindow` (30),
`rateWindow` (60 s), `batchSize` (100), `queueCapacity` (4000), `maxAttempts` (3), `requestTimeout`
(10 s), `spoolPath`, `spoolCapacity` (1000), `installIdLifetime` and `optOutLifetime` (390 days
each), `logger`, `onError`. Under `app`: `autoFlushOnBackground`. Each language keeps its own
spelling for durations — `TimeSpan` in .NET, `TimeInterval` in Swift, a `…Ms` suffix in Kotlin and
JS — and nothing else varies.

Before sending, every client applies the collector's own ceilings — it applies them again on arrival,
so this only keeps the wire free of what would be dropped there: 64 characters per event name,
property key and string value, two ASCII letters for
`country` (an unreadable one is stored as unknown, never guessed). Text is trimmed and stripped of
control characters, which Postgres rejects in `text` and `jsonb`; NaN and the infinities are dropped,
being unrepresentable in `jsonb`.

## Answers

| Status | Meaning for a client |
|---|---|
| `204` | Accepted, or silently dropped — including a body that could not be read at all: malformed, over the size ceiling, or carrying a duplicate key. There is no difference on the wire, by design. |
| `404` | Unknown source. Permanent: fix the configuration, do not retry. |
| `429` | Rate limited, per client address. Honour `Retry-After` (default window: 60s). |
| `5xx` | Retry a few times with backoff, then drop. |

[OBLIGATIONS.md](OBLIGATIONS.md) covers what a client must guarantee. Adding a source, an event or a
property is done on the collector, by whoever operates it.
