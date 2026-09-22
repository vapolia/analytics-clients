# Clients

This document explains how to integrate a `Vapolia Analytics` client into your app or your website.  
To understand the clients' internals see [README.code.md](README.code.md) instead.

[OBLIGATIONS.md](OBLIGATIONS.md) states what integrating a client commits you to, country by country, and shows the welcome popup each regime calls for.

Clients exist in the following language:

| Path | Target |
|---|---|
| [`go/`](go/README.md) | Go 1.24+, stdlib only. Server-side callers. |
| [`kotlin/`](kotlin/README.md) | Android, minSdk 24, no dependencies. `com.vapolia.analytics:analytics` |
| [`swift/`](swift/README.md) | iOS 15+ / macCatalyst, no dependencies. SwiftPM. Has its own `PrivacyInfo.xcprivacy` |
| [`js/`](js/README.md) | Expo / React Native, TypeScript. `@vapolia/analytics` on npm. Requires AsyncStorage, the `expo-*` packages are optional. |
| [`dotnet/`](dotnet/README.md) | .NET 10: MAUI/Android/iOS/Windows apps `Vapolia.Analytics.Client` and Blazor/ASP.NET Core servers `Vapolia.Analytics.Client.AspNetCore` on nuget  |


## The contract every client implements

1. One request per (install id, device, batch context). 
   Global properties sit on the batch, events in `events[]`, at most 100 events per request. 
   A source may raise that ceiling. The collector silently cuts whatever a client sends past it.
2. Never block the caller, never surface an error.
   The collector answers `204` even when its own write failed, so a client has nothing to act on. Queue, batch, retry a few times, then drop.
3. Whitelisted names only. 
   Event names, property keys and batch-context keys come from the source's three lists in the collector's configuration.
   Anything else is dropped server-side, silently.
4. **One key, once.** A body carrying the same property twice is refused whole. Serialising a map
   cannot produce this, assembling JSON by hand can.
5. **Scalars only in `props` and `context`**: string (≤64 chars), finite number, bool. Max 12 each.
   `props` describes one event. `context` describes the installation for the whole batch, so a change of context starts another batch.
   The body's top level is a **closed list**: `installId`, `country`, `context`, `events`. Nothing else
   is accepted.
6. **`installId` is a device-generated UUID, rotated by the device**, never an account id nor anything derived from one. 
   `country` is the device's region setting, never a geolocation of the IP.
7. **No client emits an event of its own.** `first_open` and `app_open` are named and placed by the
   app. Each client exposes what the app needs for it: a first-run flag, and the lifecycle it already
   watches in order to flush.
8. **A client sends nothing while opted out, and mints no id.** The refusal is stored on the device.
   Under a regime that asks before anything may be stored, `defaultOptedOut` is what an unanswered
   question reads as: nothing is written until the answer, and an acceptance takes effect without a
   restart. Which countries ask first is in [OBLIGATIONS.md](OBLIGATIONS.md).
9. **Excluded countries are refused client-side too.** The deployment lists them in `excludedCountries`, globally or
   on the source when it needs a list of its own. The app passes whichever applies to its client, which then sends
   nothing from there. The collector enforces it as well. No client hardcodes a country. Which countries, and why, is
   in [OBLIGATIONS.md](OBLIGATIONS.md).
10. **One token, one header, for both roles.** Every credential — a server's own key or a build's own token — is sent
   as `Authorization: Bearer <token>`. The collector tells the two apart from the token's own claims, not from how it
   arrived. Either one names the platform and the build, so no client sends those in the body. A missing, expired or
   revoked build token gives a `401`: that build is no longer measured. A client presenting no credential is
   untrusted, and limited by address.

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

Every field but `installId` and `events` is optional. `installId` is optional too for a server sending
under its server token: its events then belong to no installation. A batch with neither an install id
nor a token is dropped. The platform, the build, the OS version, the device class, the store **and the
locale** come from the `Authorization` token alone, never the body. A deployment whose builds carry no
token does not measure those axes at all. `context` keys are the source's own, and the collector knows
nothing about what is in it. `country` stays top-level because it is the axis of the source's
`excludedCountries` filter. Anything a caller wants to add about its own client — a browser and its
version, for a server relay — goes in `context` or in an event's `props`, under a whitelisted key.

`ts` defaults to the server's clock, and is dropped when more than 5 minutes ahead (never recalibrated)
or more than 7 days old. `tz` is the UTC offset in minutes east of UTC when the event happened (UTC+2
is 120), sent apart from `ts`, which stays UTC. An absent `tz` means unknown, not UTC. The collector
stamps every batch with its own `received_at`.

## Sending

Events queue in memory and leave in the background, grouped by install id, device and batch context.
A group is sent when it reaches the batch size, when the flush interval elapses (30 s everywhere), or
when the app asks for a flush. Losses are counted, never reported: each client exposes counters.

| Situation | What a client does |
|---|---|
| Queue full | Drops the newest event, never blocks the caller. |
| Own rate window saturated | Drops until the window ends. `maxEventsPerWindow` (30) counts in a fixed `rateWindow` (60 s). Zero disables it, and is the default server-side. |
| `429`, `5xx`, network | Retried with backoff, a few attempts, then dropped — kept for the next flush on mobile. |
| `404` and other `4xx` | Permanent, dropped at once. |

The mobile and js clients write what is unsent to disk, so a restart resumes. The js one spools a few
hundred milliseconds after each event, rather than on background alone.

Every client names these the same way:
`ingestionUrl` (`https://baseUrl/sourceName`), `token`, `enabled`, `defaultOptedOut`,
`excludedCountries`, `context`,
`seedInstallId`, and — under `advanced` — `flushInterval` (30 s), `maxEventsPerWindow` (30),
`rateWindow` (60 s), `batchSize` (100), `queueCapacity` (4000), `maxAttempts` (3), `requestTimeout`
(10 s), `spoolPath`, `spoolCapacity` (1000), `installIdLifetime` and `optOutLifetime` (390 days
each), `logger`, `onError`. Under `app`: `autoFlushOnBackground`. Each language keeps its own
spelling for durations: `TimeSpan` in .NET, `TimeInterval` in Swift, a `…Ms` suffix in Kotlin and JS.

Before sending, every client applies the collector's own ceilings: 64 characters per event name,
property key and string value, two ASCII letters for `country` (an unreadable one is stored as
unknown). Text is trimmed and stripped of control characters, and NaN and the infinities are dropped.
The collector applies the same ceilings on arrival.

## Answers

| Status | Meaning for a client |
|---|---|
| `204` | Accepted, or silently dropped — including a body that could not be read at all: malformed, over the size ceiling, or carrying a duplicate key. There is no difference on the wire. |
| `404` | Unknown source. Permanent: fix the configuration, do not retry. |
| `429` | Rate limited, per client address. Honour `Retry-After` (default window: 60 s). |
| `5xx` | Retry a few times with backoff, then drop. |

[OBLIGATIONS.md](OBLIGATIONS.md) covers what a client must guarantee. Adding a source, an event or a
property is done on the collector, by whoever operates it.
