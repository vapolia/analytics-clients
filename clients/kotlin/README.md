# Kotlin client (Android)

A dependency-free Android client for the collector (`POST {endpoint}/{source}`).

## Summary

```kotlin
// build.gradle.kts
implementation("com.vapolia.analytics:analytics:1.0.0")

// Application.onCreate
Analytics.start(this, ingestionUrl = "https://analytics.example.com/<sourceName>")

// what is true of the installation, read again for every event
Analytics.context = { mapOf("plan" to user.plan, "tutorial_done" to progress.done) }

// your own opens: the client names no event
if (Analytics.isFirstRun) { Analytics.track("first_open"); Analytics.markSeen() }
Analytics.track("app_open")
Analytics.onForeground = { Analytics.track("app_open") }

// anywhere, on any thread
Analytics.track("game_end", "result" to "win", "moves" to 34)

// the opposition switch, in the settings screen
Analytics.isOptedOut = true
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
| Device context | `country`, and only `country`: the **region setting**, never a geolocation. The platform, the build, the OS version, the device class and the store come from the `Authorization` token, which names the build that was issued it — see the [contract](https://github.com/vapolia/analytics-clients/blob/main/clients/README.md#payload). |
| Time zone | Each event carries the device offset in minutes east of UTC (`tz`), read at the instant of the event, apart from its UTC `ts`. |
| `isFirstRun` | Whether the installation has been seen before, so *your* `first_open` fires once — kept apart from the id, so a renewal is not a new install. |
| `onForeground` / `onBackground` | The activity lifecycle the client already watches to flush, with its rotation guard. |
| Background | The queue is sent and spooled to disk when the last activity stops. |

The queue is sent and written down when the app goes to the background, on its own.

## API

| | |
|---|---|
| `start(context, ingestionUrl)` | Starts the sender. Idempotent. `ingestionUrl` is `https://baseUrl/sourceName`; there is no default. |
| `start(context, AnalyticsOptions(...))` | Same, with everything else configurable. |
| `track(name, vararg props: Pair<String, Any?>)` | `Analytics.track("theme_apply", "night" to true)`. |
| `track(name, props: Map<String, Any?>?)` | Same, for a map built elsewhere. |
| `flush()` / `flushBlocking(timeoutMs)` | Send what is queued, without / with waiting. |
| `isOptedOut` | The right of opposition. Before any answer it follows `requiresPriorConsent`; setting it drops the queue and forgets the id. |
| `installId` | The current id, for a support screen. Null when opted out. |
| `stats` | `accepted` / `rejected` / `dropped` / `sent` / `requests`. |
| `context = { map }` | What is true of the installation for a whole batch, read again for every event. Every key must be on the source's `context` whitelist. |
| `isFirstRun`, `markSeen()`, `onForeground`, `onBackground` | What an app needs to name its own opens. |
| `firstSeen`, `InstallAge.bucket(firstSeen, now)` | The installation's age, in buckets, for a whitelisted `context` key. |
| `seed(InstallSeed)` | Takes an id from another SDK, once, before this client ever issued one of its own. |
| `updateDevice { }` | Corrects what the device probe reported. Not for anything about the app. |
| `stop(timeoutMs)` | One last flush, then the sender stops. Rarely needed. |

`AnalyticsOptions` mirrors the .NET client, which is this repository's reference: `ingestionUrl`
(required), `token`, `seedInstallId`, `isDebugBuild`, `requiresPriorConsent`, `excludedCountries`, `context` —
and the rest under `advanced` and `app`:

```kotlin
Analytics.start(this, AnalyticsOptions(
    ingestionUrl = "https://analytics.example.com/myapp",
    token = BuildConfig.ANALYTICS_TOKEN,
    excludedCountries = setOf("KR"),
    advanced = AnalyticsAdvancedOptions(queueCapacity = 4_000, logger = LogcatLogger),
))
```

`advanced`: `flushIntervalMs` (30 s), `maxEventsPerWindow` (30), `rateWindowMs` (60 s), `batchSize`
(100, the collector's ceiling), `queueCapacity` (4000), `maxAttempts` (3), `connectTimeoutMs` /
`readTimeoutMs` (10 s each — `HttpURLConnection` has two timeouts where .NET names one), `spoolPath`
(null: the app's files dir), `spoolCapacity` (1000, zero disables the spool), `installIdLifetimeMs`
and `optOutLifetimeMs` (390 days each), `logger` (silent; pass `LogcatLogger` while integrating),
`onError` (`(Throwable?, String, Boolean)`, called on every loss next to the log).
`app`: `flushesOnBackground` (true).

## requiresPriorConsent and isOptedOut

Depending on the country, consent may need to be given before the client starts collecting data.
This is controlled by the `requiresPriorConsent` option.
Which countries require this prior consent, and what the popup says, are in
[OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md).
This option is only used while the person has not answered, so an acceptance survives the next launch.

When `requiresPriorConsent` is null, the client answers with a built-in default, as a convenience.
**That default must not be taken as a legal basis: the choice stays yours, and so does the liability for it.**

To use your own choice:
```kotlin
Analytics.start(this, AnalyticsOptions(
    ingestionUrl = "https://analytics.example.com/myapp",
    requiresPriorConsent = yourRequiresPriorConsent(locale),   // default is localeRequiresPriorConsent()
))

// the popup's two buttons
onAccept = { Analytics.isOptedOut = false }
onRefuse = { Analytics.isOptedOut = true }
```

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
- `excludedCountries` is empty by default: pass the list that applies to your app. What is in it is
  refused here as well as by the collector.
- The privacy policy, the store declarations, the opposition switch and the list of excluded countries
  are the app's obligations — see [OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md).

## Build and test

```bash
cd clients/kotlin
./gradlew :analytics:testDebugUnitTest   # JVM unit tests, no emulator
./gradlew :analytics:assembleRelease
```

The Android layer is a thin shell (`Analytics`, `InstallIdentity`, `DeviceProbe`); the queue, the
sanitizer, the encoder, the spool and the transport are plain Kotlin, which is what the tests cover.

## Publishing

`com.vapolia.analytics:analytics` is published to Maven Central

The usual path is a GitHub release: bump `VERSION_NAME` in [`gradle.properties`](https://github.com/vapolia/analytics-clients/blob/main/clients/kotlin/gradle.properties),
commit, then run the publish workflow

```bash
gh release create kotlin-v1.0.1 --title "Kotlin client 1.0.1" --notes "..."
```

It verifies that the tag and `VERSION_NAME` agree, runs the tests, and uploads.  
Then validate the release by hand on the Central Portal. 

### Setup

These gh action secrets must be setup first:  
`MAVEN_CENTRAL_USERNAME`, `MAVEN_CENTRAL_PASSWORD`, `SIGNING_IN_MEMORY_KEY`, `SIGNING_IN_MEMORY_KEY_PASSWORD`.

MAVEN_CENTRAL_* are the user/pass of the user TOKEN (not the user account) at Maven Central.  

```shell
gh secret set MAVEN_CENTRAL_USERNAME --repo vapolia/analytics-clients                                                                                                                                                                                                                                                                                                                          
gh secret set MAVEN_CENTRAL_PASSWORD --repo vapolia/analytics-clients                                                                                                                                                                                                                                                                                                                          
```

`SIGNING_IN_MEMORY_KEY` is the private GPG key in ASCII armor. Its public part must have been pushed to a key server.
Propagation between key servers takes up to a few hours, and Maven Central rejects a signature whose public key it cannot find.
Push it before the first release.

```shell
# Create a PGP key
gpg --quick-generate-key "Your Name <you@example.com>" rsa4096 sign 2y
# Backup the key and the revocation certificate (see filename on console)
gpg --armor --export-secret-keys <key-id>
# publish the public half, before any release
gpg --keyserver keys.openpgp.org --send-keys <pub-key-id>

# write the key to gh secrets
gpg --armor --export-secret-keys <key-id> | gh secret set SIGNING_IN_MEMORY_KEY --repo vapolia/analytics-clients
gh secret set SIGNING_IN_MEMORY_KEY_PASSWORD --repo vapolia/analytics-clients
```

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
