# Swift client (iOS, macCatalyst)

A dependency-free Swift client for the collector (`POST {endpoint}/{source}`).

## Summary

```swift
import VapoliaAnalytics

// application(_:didFinishLaunchingWithOptions:) or the App's init
Analytics.start(ingestionUrl: URL(string: "https://analytics.example.com/<sourceName>")!)

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
| Device context | `country`, and only `country`: the **region setting**, never a geolocation. The platform, the build, the OS version, the device class and the store come from the `Authorization` token, which names the build that was issued it — see the [contract](https://github.com/vapolia/analytics-clients/blob/main/clients/README.md#payload). |
| Time zone | Each event carries the device offset in minutes east of UTC (`tz`), read at the instant of the event, apart from its UTC `ts`. |
| `isFirstRun` | Whether the installation has been seen before, so *your* `first_open` fires once — kept apart from the id, so a renewal is not a new install. |
| `firstSeen` | When the installation was first seen, kept across id renewals. Feed it to `InstallAge.bucket(firstSeen:now:)` for a `"0"` / `"1-7"` / `"8-30"` / `"31-90"` / `"90+"` bucket, if your source whitelists a key for it. |
| `onForeground` / `onBackground` | `willEnterForeground` and `didEnterBackground`, from the observers the client already holds — not `didBecomeActive`, which also fires after a phone call or a pulled-down notification centre. |
| Background | On `didEnterBackground` the queue is sent and spooled, under a background task. |
| Privacy manifest | The package ships its own `PrivacyInfo.xcprivacy`: no tracking, *Product Interaction* not linked to the user, purpose *Analytics*, `UserDefaults` reason `CA92.1`. |

The queue is sent and written down when the app backgrounds, on its own, inside a `beginBackgroundTask`.

## API

| | |
|---|---|
| `Analytics.start(ingestionUrl:)` | Starts the sender, on the main actor. Idempotent. `ingestionUrl` is `https://baseUrl/sourceName`; there is no default. |
| `Analytics.start(_ options: AnalyticsOptions)` | Same, with everything else configurable. |
| `Analytics.track(_:_:)` | `Analytics.track("theme_apply", ["night": true])`. Props are `PropValue` — string, number or bool — and take literals. |
| `Analytics.flush()` / `await Analytics.flushAndWait()` | Send what is queued, without / with waiting. |
| `Analytics.optedOut` | The right of opposition. Off by default; setting it drops the queue and forgets the id. |
| `Analytics.installId` | The current id, for a support screen. Nil when opted out. |
| `Analytics.stats` | `accepted` / `rejected` / `dropped` / `sent` / `requests`. |
| `Analytics.context` | What is true of the installation for a whole batch, read again for every event. Every key must be on the source's `context` whitelist. |
| `Analytics.isFirstRun`, `markSeen()`, `onForeground`, `onBackground` | What an app needs to name its own opens. |
| `Analytics.firstSeen`, `InstallAge.bucket(firstSeen:now:)` | The installation's age, in buckets, for a whitelisted `context` key. |
| `Analytics.seed(_:)` | Takes an `InstallSeed` from another SDK, once, before this client ever issued an id of its own. |
| `Analytics.updateDevice { }` | Corrects what the device probe reported. Not for anything about the app. |
| `await Analytics.stop()` | One last flush, then the sender stops. Rarely needed. |

`AnalyticsOptions` mirrors the .NET client, which is this repository's reference: `ingestionUrl`
(required), `token`, `seedInstallId`, `enabled`, `excludedCountries`, `context` — and the rest under
`advanced` and `app`:

```swift
Analytics.start(AnalyticsOptions(
    ingestionUrl: URL(string: "https://analytics.example.com/myapp")!,
    token: BuildToken.value,
    excludedCountries: ["KR"],
    advanced: .init(queueCapacity: 4_000, logger: PrintLogger()),
    app: .init(autoFlushOnBackground: true)
))
```

`advanced`: `flushInterval` (30 s), `maxEventsPerWindow` (30), `rateWindow` (60 s), `batchSize` (100,
the collector's ceiling), `queueCapacity` (4000), `maxAttempts` (3), `requestTimeout` (10 s),
`spoolPath` (nil: Caches), `spoolCapacity` (1000, zero disables the spool), `installIdLifetime` and
`optOutLifetime` (390 days each), `logger` (silent; pass `PrintLogger()` while integrating),
`onError` (`(Error?, String, Bool)`, called on every loss next to the log).
`app`: `autoFlushOnBackground` (true), `backgroundScope` (nil: the client's own `beginBackgroundTask`).

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
- `excludedCountries` is empty by default: pass the list that applies to your app. What is in it is
  refused here as well as by the collector.
- The privacy policy, the App Store declarations, the opposition switch and the list of excluded
  countries are the app's obligations — see [OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md). The bundled privacy
  manifest covers the SDK's own declaration, not the app's *App Privacy* answers.

## Build and test

```bash
swift build && swift test                       # seconds, on any Mac
xcodebuild test -scheme VapoliaAnalytics -destination 'platform=iOS Simulator,name=iPhone 16'
```

The manifest declares a macOS platform alongside iOS and macCatalyst. macOS is not a shipping target
— it is what `swift test` compiles for on a Mac, which catches a compile error in seconds instead of
waiting on a simulator. CI runs both, the fast one first.

The UIKit layer is a thin shell (`Analytics`, `DeviceProbe`); the sender, the sanitizer, the encoder,
the spool and the transport are Foundation-only, which is what the 42 tests cover.

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
