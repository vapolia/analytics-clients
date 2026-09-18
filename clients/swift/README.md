# Swift client (iOS, macCatalyst)

A dependency-free Swift client for the collector (`POST {endpoint}/{source}`).

## TL;DR

```swift
import VapoliaAnalytics

// application(_:didFinishLaunchingWithOptions:) or the App's init
Analytics.start(source: "<sourceName>", endpoint: "https://analytics.example.com")

// what is true of the installation, read again for every event
Analytics.context = { ["plan": .string(user.plan), "tutorial_done": .bool(progress.done)] }

// your own opens: the client names no event
if Analytics.isFirstRun { Analytics.track("first_open"); Analytics.markSeen() }
Analytics.track("app_open")
Analytics.onForeground = { Analytics.track("app_open") }

// anywhere, from any thread
Analytics.track("game_end", ["result": "win", "moves": 34])

// the opposition switch, in the settings screen
Analytics.optedOut = true
```

The client generates and renews the installation id, fills in the device, sends in the background,
writes what it could not send to disk, and picks it up on the next launch.

**It emits no event of its own.** An event name belongs to the source's whitelist, and a client that
invented one would send something the collector drops without a word — so `first_open` and `app_open`
are yours to name and to place, from `isFirstRun` and `onForeground` above.

- `track` never blocks, never throws, never reports an error: a measurement that cannot be recorded
  is not something a caller can act on. Losses show up in `Analytics.stats`.
- Event names and property keys must be on the source's whitelist in
  the collector's configuration; anything else is dropped, silently, by
  the collector.
- Nothing to call on shutdown: the queue is flushed and written down when the app backgrounds, inside
  a `beginBackgroundTask` so the send has a few seconds of network left.

## Install

Swift Package Manager, iOS 15+ / macCatalyst 15+:

```swift
.package(url: "https://github.com/vapolia/analytics-clients", from: "1.0.0")
```

then add `VapoliaAnalytics` to the target's dependencies. In Xcode: *File → Add Package
Dependencies…* with the same URL.

The manifest lives at the repository root because SwiftPM requires it there; the sources are in
`clients/swift`, next to the other clients.

## What it does for you

| | |
|---|---|
| Installation id | A random UUID in `UserDefaults`, renewed after 390 days — the 13-month ceiling, with a margin for clock drift. **Not** in the Keychain: a Keychain item survives the app being deleted, which would make the id outlive the installation it names. |
| Device context | `platform` (`ios` or `maccatalyst`), `osVersion`, `deviceClass` (idiom), `locale` (BCP 47), `country` (the **region setting**, never a geolocation), `store` (`apple`), `build` (`CFBundleVersion`). |
| Time zone | Each event carries the device offset in minutes east of UTC (`tz`), read at the instant of the event, apart from its UTC `ts`. |
| `isFirstRun` | Whether the installation has been seen before, so *your* `first_open` fires once — kept apart from the id, so a renewal is not a new install. |
| `onForeground` / `onBackground` | `willEnterForeground` and `didEnterBackground`, from the observers the client already holds — not `didBecomeActive`, which also fires after a phone call or a pulled-down notification centre. |
| Background | On `didEnterBackground` the queue is sent and spooled, under a background task. |
| Privacy manifest | The package ships its own `PrivacyInfo.xcprivacy`: no tracking, *Product Interaction* not linked to the user, purpose *Analytics*, `UserDefaults` reason `CA92.1`. |

The queue is sent and written down when the app backgrounds, on its own, inside a `beginBackgroundTask`.

## API

| | |
|---|---|
| `Analytics.start(source:endpoint:)` | Starts the sender, on the main actor. Idempotent. `endpoint` is the collector's base URL; there is no default. |
| `Analytics.start(_ config: AnalyticsConfig)` | Same, with everything else configurable. |
| `Analytics.track(_:_:)` | `Analytics.track("theme_apply", ["night": true])`. Props are `PropValue` — string, number or bool — and take literals. |
| `Analytics.flush()` / `await Analytics.flushAndWait()` | Send what is queued, without / with waiting. |
| `Analytics.optedOut` | The right of opposition. Off by default; setting it drops the queue and forgets the id. |
| `Analytics.installId` | The current id, for a support screen. Nil when opted out. |
| `Analytics.stats` | `accepted` / `rejected` / `dropped` / `sent` / `requests`. |
| `Analytics.context` | What is true of the installation for a whole batch, read again for every event. Every key must be on the source's `context` whitelist. |
| `Analytics.isFirstRun`, `markSeen()`, `onForeground`, `onBackground` | What an app needs to name its own opens. |
| `Analytics.updateDevice { }` | Corrects what the device probe reported. Not for anything about the app. |
| `await Analytics.stop()` | One last flush, then the sender stops. Rarely needed. |

`AnalyticsConfig`: `source` (required), `endpoint`, `buildToken`, `excludedCountries`, `flushInterval` (30s), `batchSize` (100, the
collector's ceiling), `queueCapacity` (2000), `spoolCapacity` (1000), `maxAttempts` (3),
`requestTimeout` (10s), `logger` (silent; pass `PrintLogger()` while integrating).

## Failure behaviour

Measurement must never fail a user action, so nothing surfaces an error:

- **Full queue** — the newest event is dropped, `track` returns. Counted in `stats.dropped`.
- **5xx, network error, 429** — retried up to `maxAttempts`, honouring `Retry-After`, then **kept**
  for the next flush. On a phone a failed send usually means no network, and the next launch is a
  better bet than a loss. Same choice as the Kotlin client; the Go one drops instead.
- **404 / 4xx** — permanent (unknown source, malformed payload); dropped without a retry.
- **Events older than 7 days** — dropped before being sent; the collector refuses them anyway.
- **Process killed** — what was pending is in Caches and goes out on the next launch. It is read back
  once: a lost count beats a double count. Caches, not Application Support, so unsent counters are
  never backed up to iCloud and restored onto another device.

## What this client cannot check for you

- Event names and property keys are whitelisted server-side. A typo is not an error, it is silence.
- Batch-context keys are whitelisted server-side too, under `context:` — a key nobody declared is
  silence, exactly like an event name.
- A context value must stay a bucket, never a birth date, an account id, or anything derived from one.
- Countries in the excluded list (the source's `excludedCountries`, e.g. `KR` for a 13+ app: PIPA requires a guardian's
  consent under 14) are refused here as well as by the collector. The list is empty by default: pass it.
- The privacy policy, the App Store declarations and the opposition switch in the UI are the app's
  obligations — see [OBLIGATIONS.md](../OBLIGATIONS.md). The bundled privacy manifest covers the
  SDK's own declaration, not the app's *App Privacy* answers.

## Build and test

```bash
xcodebuild test -scheme VapoliaAnalytics -destination 'platform=iOS Simulator,name=iPhone 16'
```

Tests need a simulator: the package supports iOS and macCatalyst only, so `swift test` on a Mac has
no destination to build for. [`.github/workflows/swift-client-test.yml`](../../.github/workflows/swift-client-test.yml)
runs exactly that, picking whatever iPhone simulator the runner has.

The UIKit layer is a thin shell (`Analytics`, `DeviceProbe`); the queue, the sanitizer, the encoder,
the spool and the transport are Foundation-only, which is what the 24 tests cover.

> Written on a Windows workstation with no Swift toolchain: this code has **not** been compiled
> locally. The CI job above is what proves it builds — run it before relying on the client.

## Versioning

SwiftPM resolves versions from git tags, so a release is a tag on this repository:

```bash
git tag 1.0.0 && git push origin 1.0.0
```

Plain `1.0.0`, not `kotlin-v1.0.0`: SwiftPM only understands bare semantic-version tags. That is also
why the Kotlin client's tags carry a prefix — the two schemes have to coexist in one repository.

There is nothing to publish, so a pre-release is a tag and nothing more:

```bash
git tag 1.1.0-beta.1 && git push origin 1.1.0-beta.1
```

`from:` and `upToNextMajor` never resolve a pre-release, so it reaches nobody who did not ask for it
by name:

```swift
.package(url: "...", exact: "1.1.0-beta.1")
```

The flip side is that a pushed tag is already distributed: there is no gate, and anyone who knows the
number can pin it.
