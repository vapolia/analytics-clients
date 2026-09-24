# Clients

This document is for developers who integrate a `Vapolia Analytics` client into an app or a website. It describes what every client sends and how it behaves.
The clients' internals are in [README.code.md](README.code.md).
What integrating a client commits you to, country by country, and the welcome popup each regime calls for, are in [OBLIGATIONS.md](OBLIGATIONS.md).

## Summary

1. Pick the client for your platform in the table below and follow its README.
2. Declare on the collector every event name, property key and context key your app sends. Anything undeclared is dropped silently.
3. Show the welcome popup and wire the opposition switch as [OBLIGATIONS.md](OBLIGATIONS.md) describes.
4. Pass `excludedCountries` and, if needed, `requiresPriorConsent` to the client.
5. Read the client's loss counters when events seem missing: the collector answers `204` even when it drops a batch.

| Path | Target |
|---|---|
| [`go/`](go/README.md) | Go 1.24+, stdlib only, for server-side callers. |
| [`kotlin/`](kotlin/README.md) | Android, minSdk 24, no dependencies. Package `com.vapolia.analytics:analytics`. |
| [`swift/`](swift/README.md) | iOS 15+ and Mac Catalyst, no dependencies, SwiftPM. Ships its own `PrivacyInfo.xcprivacy`. |
| [`js/`](js/README.md) | Expo and React Native, TypeScript. Package `@vapolia/analytics` on npm. Requires AsyncStorage or a storage passed as `advanced.storage`. The `expo-*` packages are optional. |
| [`dotnet/`](dotnet/README.md) | .NET 10. `Vapolia.Analytics.Client` for MAUI, Android, iOS and Windows apps. `Vapolia.Analytics.Client.AspNetCore` for Blazor and ASP.NET Core servers. Both on NuGet. |

## The contract every client implements

1. **One request per batch.** A batch groups the events of one install id, one device and one batch context. It carries at most 100 events. A source may raise that ceiling, and the collector silently drops whatever exceeds it.
2. **The caller is never blocked and never sees an error.** The collector answers `204` even when its own write failed, so a client has nothing to act on. A client queues, batches, retries a few times, then drops.
3. **Names are whitelisted.** Event names, property keys and context keys come from the three lists of the source in the collector's configuration. The collector drops anything else silently.
4. **A key appears once.** The collector refuses a whole body that carries the same key twice. Serialising a map cannot produce this, but JSON assembled by hand can.
5. **`props` and `context` hold scalars only.** A value is a string of at most 64 characters, a finite number or a boolean. Each holds at most 12 keys. `props` describes one event. `context` describes the installation for the whole batch, so a change of context starts a new batch.
6. **The top level of the body is closed.** It holds `installId`, `country`, `context` and `events`, and nothing else.
7. **`installId` is a UUID generated and rotated by the device.** It is never an account id nor anything derived from one. `country` is the device's region setting, never a geolocation of the IP.
8. **A client emits no event of its own.** The app names and places `first_open` and `app_open`. Each client exposes a first-run flag and the lifecycle events it already watches to flush.
9. **An opted-out client sends nothing and creates no id.** The refusal is stored on the device. Under a regime that requires consent before anything is stored, `requiresPriorConsent` gives the answer while the person has not chosen: nothing is written until they answer, and an acceptance applies without a restart. Left null, `requiresPriorConsent` is computed from the device locale and the list in [OBLIGATIONS.md](OBLIGATIONS.md).
10. **Excluded countries are refused on the client too.** The deployment lists them in `excludedCountries`, globally or per source. The app passes the list that applies to it, and the client sends nothing from those countries. The collector enforces the same list. No client hardcodes a country. [OBLIGATIONS.md](OBLIGATIONS.md) lists which countries and why.
11. **Every credential is sent as `Authorization: Bearer <token>`.** A server key and a build token use the same header, and the collector tells them apart from the token's claims. A missing, expired or revoked build token gets a `401`, and that build is no longer measured. A client without a credential is untrusted and rate-limited by address.

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

| Field | Rule |
|---|---|
| `installId` | Required, except for a server sending with its server token: its events then belong to no installation. The collector drops a batch that has neither an install id nor a token. |
| `country` | Optional. It stays at the top level because the source's `excludedCountries` filter uses it. |
| `context` | Optional. Its keys are the source's own, and the collector does not interpret them. |
| `events` | Required. |
| `ts` | UTC. Defaults to the server's clock. The collector drops an event more than 5 minutes in the future or more than 7 days old, and never corrects the clock. |
| `tz` | The offset in minutes east of UTC when the event happened (UTC+2 is `120`). An absent `tz` means unknown, not UTC. |

The platform, the build, the OS version, the device class, the store and the locale come from the `Authorization` token only, never from the body. A deployment whose builds carry no token does not measure them.
Anything else a caller wants to send about its own client, such as a browser and its version for a server relay, goes in `context` or in an event's `props`, under a whitelisted key.
The collector stamps every batch with its own `received_at`.

## Sending

Events wait in memory and leave in the background, grouped into batches.
A batch leaves when it reaches the batch size, when the flush interval elapses (30 s on every client), or when the app asks for a flush.
Losses are counted: each client exposes counters and an `onError` callback.

| Situation | What the client does |
|---|---|
| Queue full | Drops the newest event and returns at once. |
| Own rate window saturated | Drops events until the window ends. `maxEventsPerWindow` (30) counts events in a fixed `rateWindow` (60 s). Zero disables it, and zero is the server default. |
| `429`, `5xx`, network error | Retries with backoff up to `maxAttempts`. After that, a server client drops the events, and a mobile or JS client keeps them for the next flush. |
| `404` and other `4xx` | Drops the batch at once. |

The mobile and JS clients write unsent events to disk, so a restart resumes them. The JS client writes them a few hundred milliseconds after each event, the mobile clients when the app goes to the background.

Before sending, every client applies the collector's ceilings:

- Event names, property keys and string values are trimmed, stripped of control characters and cut at 64 characters.
- `country` must be two ASCII letters. Anything else is sent as unknown.
- NaN and the infinities are dropped.

The collector applies the same ceilings on arrival.

## Options

Every client uses these names. Each language keeps its own type for durations: `TimeSpan` in .NET, `TimeInterval` in Swift, a `…Ms` suffix in Kotlin and JS.

| Group | Option | Default |
|---|---|---|
| Main | `ingestionUrl` (`https://baseUrl/sourceName`) | |
| Main | `token` | |
| Main | `isDebugBuild` | `false` |
| Main | `requiresPriorConsent` | from the locale |
| Main | `excludedCountries` | empty |
| Main | `context` | |
| Main | `seedInstallId` | |
| `advanced` | `flushInterval` | 30 s |
| `advanced` | `maxEventsPerWindow` | 30 |
| `advanced` | `rateWindow` | 60 s |
| `advanced` | `batchSize` | 100 |
| `advanced` | `queueCapacity` | 4000 |
| `advanced` | `maxAttempts` | 3 |
| `advanced` | `requestTimeout` | 10 s |
| `advanced` | `spoolPath` | |
| `advanced` | `spoolCapacity` | 1000 |
| `advanced` | `installIdLifetime` | 390 days |
| `advanced` | `optOutLifetime` | 390 days |
| `advanced` | `logger` | |
| `advanced` | `onError` | |
| `app` | `flushesOnBackground` | `true` |

## Answers

| Status | Meaning for a client |
|---|---|
| `204` | Accepted, or silently dropped. A body the collector could not read at all (malformed, over the size ceiling, or with a duplicate key) also gets a `204`. |
| `401` | The build token is missing, expired or revoked. |
| `404` | Unknown source. Fix the configuration. Retrying does not help. |
| `429` | Rate limited per client address. Honour `Retry-After`. Without it, wait 60 s. |
| `5xx` | Retry a few times with backoff. |

Adding a source, an event or a property is done on the collector, by whoever operates it.
