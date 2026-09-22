# Clients — notes for whoever codes in this repo

Design decisions and internal constraints. The contract itself is in [README.md](README.md), which is
written for the app integrating a client.

## Summary

- The .NET client is the reference. The others follow its option names, and nothing but the duration
  spelling varies between languages.
- Every client has a test asserting the body's top level is exactly `installId`, `country`, `context`,
  `events`. A sixth key is a bug in that client, not a variation.
- The silences (dropped names, `204` on a failed write, `204` on an unreadable body) are deliberate.
  Do not add an error path for them.
- Client-side ceilings only keep the wire clean. The collector applies them again on arrival, so a gap
  between the two is a cosmetic bug, never a data bug.

## Layout

Each client is independent: no shared build, no shared package. A fix in one is ported by hand.

The js client spools continuously because a JS runtime has no `beginBackgroundTask`. The Kotlin and
Swift clients hold the install id, the device context and the lifecycle, none of which a server-side
client has.

The rate window is per client, and drops rather than letting the whole address get throttled: behind a
carrier NAT, a `429` hits every other installation. It is off by default server-side, where one process
speaks for every visitor.

The Swift package keeps `Package.swift` at the repository root, as SwiftPM requires. Everything else
lives under `clients/swift/`.

## Options

The .NET client is the reference the others follow. Each language keeps its own spelling for
durations — `TimeSpan` in .NET, `TimeInterval` in Swift, a `…Ms` suffix in Kotlin and JS — and nothing
else varies: same names, same defaults, same nesting under `advanced` and `app`. Adding an option to
one client without the others is a divergence to fix, not a feature.

## Running the tests

| Client | Command |
|---|---|
| .NET | `dotnet run --project clients/dotnet/Vapolia.Analytics.Client.Tests` |
| Kotlin | `./gradlew :analytics:testDebugUnitTest`, from `clients/kotlin` |
| Swift | `swift test`, on macOS, from the repository root where `Package.swift` lives |
| JS | `pnpm test`, from `clients/js` |
| Go | `go test ./...`, from `clients/go` |

The .NET tests run on Microsoft Testing Platform: the test project is an executable and is run as
one. `dotnet test` reports "Zero tests ran".

## The unanswered state

`optedOut` has three states, held in two: an explicit answer in storage, and `defaultOptedOut` in the
options for when there is none. The unanswered state reads as opted out under a consent regime and
writes nothing — a stored refusal would be an answer nobody gave, and it would also start the
390-day refusal clock. The install id is minted on the first `track`, never at `start`, which is what
makes an unanswered start harmless; the js client is the exception and mints in `Identity.load()`, so
it returns before that when unanswered.

`start` clears the spool when the client comes up opted out: what a previous session wrote down must
not leave under an answer that has since changed, or has not been given.

The web client holds the same three states in one cookie — `1` refused, `0` accepted, absent
unanswered — and an acceptance is written rather than deleted, so it outranks `DefaultOptedOut` on
the next request. Its `DefaultOptedOutForCountry` exists because one site serves every regime at
once, while an app binary runs under one region setting at a time.

## Why the body is closed

The top level is `installId`, `country`, `context`, `events`. Every client has a test asserting exactly
that. The platform, the build, the OS version, the device class, the store and the locale come from the
`Authorization` token instead: a build that names itself could name anything, a token cannot. A
deployment whose builds carry no token does not measure those axes at all, which is the accepted price.

`country` stays top-level because it is the axis of the `excludedCountries` filter. It is the one thing
the body still says about the device.

A server sending under its server token may omit `installId`. Its events then carry `install_id` NULL.

## Storage constraints behind the client-side ceilings

Every client trims and strips text before sending, and drops NaN and the infinities. This is not
validation — the collector applies the same ceilings on arrival — it only keeps the wire free of what
would be dropped there.

- Control characters: Postgres rejects them in `text` and `jsonb`.
- NaN, `+Inf`, `-Inf`: unrepresentable in `jsonb`.
- `context` is stored as a single `jsonb` column. The collector knows nothing about its keys, which is
  why the whitelist lives in the source's configuration rather than in a schema.

## Duplicated enforcement

`excludedCountries` is enforced client-side and again on the collector. The duplication is intentional:
a client with a stale list must not be the only guard.

Same for the per-name whitelists — a client cannot know the source's lists, so it sends what the app
gives it and the collector drops the rest, silently.
