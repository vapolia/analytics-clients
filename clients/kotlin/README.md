# Kotlin client (Android)

A dependency-free Android client for the collector (`POST {endpoint}/{source}`).

## TL;DR

```kotlin
// build.gradle.kts
implementation("com.vapolia.analytics:analytics:1.0.0")

// Application.onCreate
Analytics.start(this, source = "<sourceName>", endpoint = "https://analytics.example.com")

// what is true of the installation, read again for every event
Analytics.context = { mapOf("plan" to user.plan, "tutorial_done" to progress.done) }

// your own opens: the client names no event
if (Analytics.isFirstRun) { Analytics.track("first_open"); Analytics.markSeen() }
Analytics.track("app_open")
Analytics.onForeground = { Analytics.track("app_open") }

// anywhere, on any thread
Analytics.track("game_end", "result" to "win", "moves" to 34)

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
- Nothing to call on shutdown: the queue is flushed and written down when the app goes to the
  background.

## Install

```kotlin
repositories { mavenCentral() }

dependencies {
    implementation("com.vapolia.analytics:analytics:1.0.0")
}
```

minSdk 24, no transitive dependency, and the `INTERNET` permission is merged in from the library
manifest.

## What it does for you

| | |
|---|---|
| Installation id | A random UUID in the app's own `SharedPreferences`, renewed after 390 days — the 13-month ceiling, with a margin for clock drift. Never derived from an account, a device id, or an advertising id. |
| Device context | `platform`, `osVersion`, `deviceClass` (`sw600dp` = tablet), `locale`, `country` (the **region setting**, never a geolocation), `store` (Play or other), `build` (the store build number). |
| Time zone | Each event carries the device offset in minutes east of UTC (`tz`), read at the instant of the event, apart from its UTC `ts`. |
| `isFirstRun` | Whether the installation has been seen before, so *your* `first_open` fires once — kept apart from the id, so a renewal is not a new install. |
| `onForeground` / `onBackground` | The activity lifecycle the client already watches to flush, with its rotation guard. |
| Background | The queue is sent and spooled to disk when the last activity stops. |

The queue is sent and written down when the app goes to the background, on its own.

## API

| | |
|---|---|
| `start(context, source, endpoint)` | Starts the sender. Idempotent. `endpoint` is the collector's base URL; there is no default. |
| `start(context, AnalyticsConfig(...))` | Same, with everything else configurable. |
| `track(name, vararg props: Pair<String, Any?>)` | `Analytics.track("theme_apply", "night" to true)`. |
| `track(name, props: Map<String, Any?>?)` | Same, for a map built elsewhere. |
| `flush()` / `flushBlocking(timeoutMs)` | Send what is queued, without / with waiting. |
| `optedOut` | The right of opposition. Off by default; setting it drops the queue and forgets the id. |
| `installId` | The current id, for a support screen. Null when opted out. |
| `stats` | `accepted` / `rejected` / `dropped` / `sent` / `requests`. |
| `context = { map }` | What is true of the installation for a whole batch, read again for every event. Every key must be on the source's `context` whitelist. |
| `isFirstRun`, `markSeen()`, `onForeground`, `onBackground` | What an app needs to name its own opens. |
| `updateDevice { }` | Corrects what the device probe reported. Not for anything about the app. |
| `stop(timeoutMs)` | One last flush, then the sender stops. Rarely needed. |

`AnalyticsConfig`: `source` (required), `endpoint`, `buildToken`, `excludedCountries`, `flushIntervalMs` (30s), `batchSize` (100, the
collector's ceiling), `queueCapacity` (2000), `spoolCapacity` (1000), `maxAttempts` (3),
`connectTimeoutMs` / `readTimeoutMs` (10s), `logger` (silent; pass
`LogcatLogger` while integrating).

## Failure behaviour

Measurement must never fail a user action, so nothing surfaces an error:

- **Full queue** — the newest event is dropped, `track` returns. Counted in `stats.dropped`.
- **5xx, network error, 429** — retried up to `maxAttempts`, honouring `Retry-After`, then **kept**
  for the next flush. On a phone a failed send usually means no network, and the next launch is a
  better bet than a loss. This is where the Android client parts from the Go one, which drops.
- **404 / 4xx** — permanent (unknown source, malformed payload); dropped without a retry.
- **Events older than 7 days** — dropped before being sent; the collector refuses them anyway.
- **Process killed** — what was pending is on disk and goes out on the next launch. It is read back
  once: a lost count beats a double count.

## What this client cannot check for you

- Event names and property keys are whitelisted server-side. A typo is not an error, it is silence.
- Batch-context keys are whitelisted server-side too, under `context:` — a key nobody declared is
  silence, exactly like an event name.
- A context value must stay a bucket, never a birth date, an account id, or anything derived from one.
- Countries in the excluded list (the source's `excludedCountries`, e.g. `KR` for a 13+ app: PIPA requires a guardian's
  consent under 14) are refused here as well as by the collector. The list is empty by default: pass it.
- The privacy policy, the store declarations and the opposition switch in the UI are the app's
  obligations — see [OBLIGATIONS.md](../OBLIGATIONS.md).

## Build and test

```bash
cd clients/kotlin
./gradlew :analytics:testDebugUnitTest   # JVM unit tests, no emulator
./gradlew :analytics:assembleRelease
```

The Android layer is a thin shell (`Analytics`, `InstallIdentity`, `DeviceProbe`); the queue, the
sanitizer, the encoder, the spool and the transport are plain Kotlin, which is what the tests cover.

## Publishing

`com.vapolia.analytics:analytics`, to Maven Central through the
[vanniktech plugin](https://vanniktech.github.io/gradle-maven-publish-plugin/).

The usual path is a GitHub release: bump `VERSION_NAME` in [`gradle.properties`](gradle.properties),
commit, then

```bash
gh release create kotlin-v1.0.1 --title "Kotlin client 1.0.1" --notes "..."
```

which runs [`.github/workflows/kotlin-client-publish.yml`](../../.github/workflows/kotlin-client-publish.yml) — it
refuses to publish if the tag and `VERSION_NAME` disagree, runs the tests again, and uploads. The
release itself is then validated by hand on the Central Portal. Its four secrets:
`MAVEN_CENTRAL_USERNAME`, `MAVEN_CENTRAL_PASSWORD`, `SIGNING_IN_MEMORY_KEY`,
`SIGNING_IN_MEMORY_KEY_PASSWORD`.

### Pre-releases are snapshots

Maven Central has no notion of a pre-release, and a released version is immutable — the number is
spent for good. So a beta is a `-SNAPSHOT`, which `publishToMavenCentral` routes to the Central
Portal's snapshots repository rather than staging as a release:

```bash
gh release create kotlin-v1.1.0-SNAPSHOT --prerelease --title "Kotlin client 1.1.0-SNAPSHOT" --notes "..."
```

with `VERSION_NAME=1.1.0-SNAPSHOT`. The workflow refuses the mismatch both ways. SNAPSHOTs must be
enabled once for the namespace, in the Central Portal: the three dots next to `com.vapolia`, *Enable
SNAPSHOTs*.

A snapshot is not served by the release repository, so a consumer opts in explicitly:

```kotlin
// settings.gradle.kts
dependencyResolutionManagement {
    repositories {
        mavenCentral()
        maven("https://central.sonatype.com/repository/maven-snapshots/")
    }
}
```

By hand:

```bash
./gradlew publishToMavenLocal                     # try it against a real app first
./gradlew publishToMavenCentral --no-configuration-cache
```

Credentials never live in the repository — put them in `~/.gradle/gradle.properties`:

```properties
mavenCentralUsername=<user token from central.sonatype.com>
mavenCentralPassword=<its password>
signingInMemoryKey=<armoured GPG secret key, newlines stripped>
signingInMemoryKeyPassword=<its passphrase>
```

The namespace `com.vapolia` must be verified on the Central Portal (a DNS TXT record on the matching
domain) before the first publish.
