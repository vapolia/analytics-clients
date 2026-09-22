# Expo / React Native client

A dependency-free TypeScript client for the collector (`POST {endpoint}/{source}`).

## Summary

```ts
import Analytics from '@vapolia/analytics';

// once, as early as possible — awaiting is optional
// context: what is true of the installation, read again for every event
void Analytics.start({
  ingestionUrl: 'https://analytics.example.com/<sourceName>',
  context: () => ({ plan: user.plan }),
});

// your own opens: the client names no event
if (Analytics.isFirstRun()) { Analytics.track('first_open'); await Analytics.markSeen(); }
Analytics.track('app_open');
Analytics.lifecycle.onForeground = () => Analytics.track('app_open');

// anywhere
Analytics.track('game_end', { result: 'win', moves: 34 });

// the opposition switch, in the settings screen
await Analytics.setOptedOut(true);
```

The client generates and renews the installation id, fills in the device, sends in the background, and
writes the queue to storage a few hundred milliseconds after every event so a killed process costs
nothing.

**It emits no event of its own.** An event name belongs to the source's whitelist, and a client that
invented one would send something the collector drops without a word — so `first_open` and `app_open`
are yours to name and to place, from `isFirstRun()` and `lifecycle.onForeground` above.

- `track` never blocks, never throws, never reports an error. Losses show up in `Analytics.getStats()`.
- Events tracked before storage answers are **queued, not lost**: the id is attached when they leave.
- Event names and property keys must be on the source's whitelist in
  the collector's configuration; anything else is dropped, silently, by
  the collector.

## Install

```bash
npx expo install @vapolia/analytics @react-native-async-storage/async-storage
```

`@react-native-async-storage/async-storage` is the one required peer: without storage there is no
stable installation id and no recovery of unsent events. It also carries its own iOS privacy manifest
entry for `UserDefaults` (reason `CA92.1`), so this client adds nothing to declare.

These are **optional** — each one only adds a field to the device context:

```bash
npx expo install expo-localization expo-device expo-application
```

| Package | What it fills in | Without it |
|---|---|---|
| `expo-localization` | `country` (the region setting) | Absent — the collector stores unknown |

It is the only one left: the platform, the build, the OS version and the device class used to be read
from `expo-device` and `expo-application`, and they now come from the `Authorization` token instead —
see the [contract](https://github.com/vapolia/analytics-clients/blob/main/clients/README.md#payload). An app that passes its own region needs no Expo package at
all:

```ts
void Analytics.start({
  ingestionUrl: 'https://analytics.example.com/<sourceName>',
  advanced: { device: { country: 'FR' } },
});
```

Works in Expo Go, EAS Build and bare React Native — it is plain JS, so a fix ships through EAS Update
without a store release.

## What it does for you

| | |
|---|---|
| Installation id | A random v4 UUID in AsyncStorage, renewed after 390 days — the 13-month ceiling, with a margin for clock drift. **Not** in SecureStore/keychain: those survive the app being deleted, which would make the id outlive the installation it names. |
| `isFirstRun()` | Whether the installation has been seen before, so *your* `first_open` fires once — kept apart from the id, so a renewal is not a new install. |
| `lifecycle.onForeground` / `onBackground` | The `AppState` transitions the client already listens to in order to flush and spool. |
| Device context | `country`, and only `country`: the **region setting**, never a geolocation. The platform, the build, the OS version, the device class and the store come from the `Authorization` token — see the [contract](https://github.com/vapolia/analytics-clients/blob/main/clients/README.md#payload). |
| Time zone | Each event carries the device offset in minutes east of UTC (`tz`), read at the instant of the event, apart from its UTC `ts`. |
| Spool | The queue is written to storage ~500 ms after each event, and read back once on the next launch. |

## API

| | |
|---|---|
| `start(options)` | Starts the client. Idempotent. Returns a promise you may ignore. |
| `track(name, props?)` | Props are scalars: string (≤64 chars), finite number, boolean. Max 12 per event. |
| `flush()` | Sends what is queued. |
| `setOptedOut(value)` / `isOptedOut()` | The right of opposition. Off by default, or `defaultOptedOut` while the person has not answered; opting out drops the queue and forgets the id. |
| `getInstallId()` | The current id, for a support screen. Undefined when opted out. |
| `getStats()` | `accepted` / `rejected` / `dropped` / `sent` / `requests`. |
| `context` (option) | What is true of the installation for a whole batch, read again for every event. Every key must be on the source's `context` whitelist. |
| `isFirstRun()`, `markSeen()`, `lifecycle` | What an app needs to name its own opens. |
| `firstSeen()`, `installAgeBucket(firstSeen, now?)` | The installation's age, in buckets, for a whitelisted `context` key. |
| `updateDevice` | Corrects what the device probe reported. Not for anything about the app. |
| `stop()` | One last flush, then the sender stops. Rarely needed. |
| `registerBackgroundFlush()` | Opt-in, see below. |

`AnalyticsOptions` mirrors the .NET client, which is this repository's reference: `ingestionUrl`
(required), `token`, `seedInstallId`, `enabled`, `defaultOptedOut`, `excludedCountries`, `context` — and the rest under
`advanced` and `app`.

`advanced`: `flushIntervalMs` (30 000), `maxEventsPerWindow` (30), `rateWindowMs` (60 000),
`batchSize` (100, the collector's ceiling), `queueCapacity` (4000), `maxAttempts` (3),
`requestTimeoutMs` (10 000), `spoolCapacity` (1000, zero disables the spool), `spoolDebounceMs`
(500), `installIdLifetimeMs` and `optOutLifetimeMs` (390 days each), `device`, `logger`, `onError`
(`(error, reason, permanent)`, called on every loss next to the log).
`app`: `autoFlushOnBackground` (true).

`spoolDebounceMs` is this client's own: it stands in for the `beginBackgroundTask` a JS runtime does
not have.

## A country that asks first

Where consent must be given before anything is stored, start with `defaultOptedOut: true` and let the
welcome popup answer. Nothing is sent and no installation id is written until it does; an acceptance
takes effect at once, with no restart, and outranks the default on the next launch.

```ts
void Analytics.start({
  ingestionUrl: 'https://analytics.example.com/myapp',
  // your own lookup, from expo-localization's regionCode
  defaultOptedOut: requiresConsent(getLocales()[0]?.regionCode),
});

const onAccept = () => Analytics.setOptedOut(false);
const onRefuse = () => Analytics.setOptedOut(true);
```

Which countries ask first, and what the popup says, are in
[OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md).

## Failure behaviour

- **Full queue** — the newest event is dropped, `track` returns. Counted in `dropped`.
- **5xx, network error, 429** — retried up to `maxAttempts`, honouring `Retry-After`, then **kept**
  for the next flush. Same choice as the Kotlin and Swift clients; the Go one drops instead.
- **404 / 4xx** — permanent (unknown source, malformed payload); dropped without a retry.
- **Events older than 7 days** — dropped before being sent; the collector refuses them anyway.
- **Process killed** — the spool goes out on the next launch. Read back once: a lost count beats a
  double count.

### The one thing this client cannot do as well as the native ones

When the app backgrounds, the JS engine is suspended within seconds and a send in flight is cut.
There is no JS equivalent of iOS's `beginBackgroundTask`, which the Swift client uses. That is why
the spool is written continuously rather than only on background: the disk, not the last flush, is
what survives.

`registerBackgroundFlush()` is available as a partial answer — it needs `expo-background-task` and
`expo-task-manager`:

```ts
import { registerBackgroundFlush } from '@vapolia/analytics';
void registerBackgroundFlush();
```

It does **not** finish the interrupted send. It asks the OS (WorkManager / BGTaskScheduler) to run a
flush later — at best every 15 minutes, only with battery and network to spare, when the system
decides. It turns "at the next launch" into "some time later today". It also requires a new build, so
it is opt-in.

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
pnpm install
pnpm test        # vitest, under Node — no emulator, no simulator
pnpm typecheck
pnpm build       # react-native-builder-bob: CommonJS + ESM + types
```

`src/core/` is plain TypeScript with no React Native import, which is what the tests cover.
`src/native/` is the thin shell that touches `react-native` and the optional Expo packages.

## Publishing

```bash
npm publish --access public       # or push a js-v* tag, see the workflow
```

The version is the tag: bump `version` in [`package.json`](https://github.com/vapolia/analytics-clients/blob/main/clients/js/package.json), commit, then publish a
GitHub release whose tag carries it.

```bash
gh release create js-v1.0.1 --title "JS client 1.0.1" --notes "..."
gh release create js-v1.1.0-beta.1 --prerelease --title "JS client 1.1.0-beta.1" --notes "..."
```

which runs the publish workflow. It refuses to publish if the tag and `package.json` disagree, runs
the tests again, and stages the version with
[provenance](https://docs.npmjs.com/generating-provenance-statements). It carries no token: npm
authenticates the run through the trusted publisher declared on npmjs.com, which names this repository
and this workflow file.

**Staging is not publishing.** The version reaches npm once a human approves it with their 2FA:

```bash
npm stage list @vapolia/analytics
npm stage approve <stage-id>        # or npm stage reject <stage-id>
```

Tests and build run on every change, in a separate workflow. Tags are prefixed because this repository also
carries the Kotlin (`kotlin-v*`) and Swift (bare `1.0.0`, as SwiftPM requires) clients; a release event carries no
tag filter, so each publish workflow starts by checking its own prefix.

### Pre-releases

A pre-release goes out under the `next` dist-tag, never `latest`. Its version must carry a semver
suffix, and the workflow refuses the mismatch both ways — a suffixed version published as a release,
or a plain one marked as a pre-release.

```bash
npm install @vapolia/analytics@next     # opt in
npm install @vapolia/analytics          # still the last stable one
```

The dist-tag is what matters: `^1.0.0` ignores pre-releases on its own, but an install with no range
follows `latest`, which is why `latest` must never point at a beta.
