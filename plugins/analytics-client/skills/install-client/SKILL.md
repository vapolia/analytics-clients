---
name: install-client
description: Wire a Vapolia analytics client SDK into an app, a website, or a service.
---

# Integrate an analytics client

For the developer wiring a client into an app or a website. The source the events land in, the names
it accepts and the metrics read out of them belong to whoever operates the collector: ask them for
the values listed below rather than looking for them.

## Summary

1. Ask the operator for the ingestion URL, the accepted names, the excluded countries and whether a
   build token is required.
2. Pick the client by where the code runs.
3. Three calls: start once, track anywhere, expose the opt-out.
4. Show the welcome popup the device's region calls for, and wire its answer to the opt-out.
5. Ask the operator to confirm the events arrived: no client can tell you.

## What to ask for first

The client sends to one URL and every name it sends is checked against a list held by the collector.
A name that is not on it is dropped without a word, and the response is `204` either way — an
integration against a missing name looks exactly like a working one. Ask for all five before writing
anything:

| | |
|---|---|
| Ingestion URL | `https://<base>/<source>` — the whole thing, source segment included. No client has a default and none starts without it. |
| Event names | The names you may send. Agree on the ones this app needs now. |
| Property keys | The keys allowed on an event, pooled across events rather than declared per event. |
| Context keys | The keys allowed on the batch context. Often none. |
| Excluded countries | The countries this app does not measure at all. Nothing is excluded by default: what you do not pass is collected. |
| Build token | Whether the source requires one. If it does, a build without it is answered `401` and is not measured. |

An event you need that is not on the list is a request to the operator, not a name to invent.

## Pick the client

| Where it runs | Client | Package |
|---|---|---|
| Android app | [Kotlin](https://github.com/vapolia/analytics-clients/blob/main/clients/kotlin/README.md) | `com.vapolia.analytics:analytics` (Maven Central, minSdk 24) |
| iOS / macCatalyst app | [Swift](https://github.com/vapolia/analytics-clients/blob/main/clients/swift/README.md) | SwiftPM `https://github.com/vapolia/analytics-clients`, product `VapoliaAnalytics` (iOS 15+) |
| Expo / React Native | [JS](https://github.com/vapolia/analytics-clients/blob/main/clients/js/README.md) | `@vapolia/analytics` + peer `@react-native-async-storage/async-storage` |
| MAUI / Android / iOS / Windows app | [.NET](https://github.com/vapolia/analytics-clients/blob/main/clients/dotnet/README.md) | `Vapolia.Analytics.Client` |
| Blazor / ASP.NET Core site | [.NET](https://github.com/vapolia/analytics-clients/blob/main/clients/dotnet/README.md) | `Vapolia.Analytics.Client.AspNetCore` |
| Game server, backend (Nakama…) | [Go](https://github.com/vapolia/analytics-clients/blob/main/clients/go/README.md) | `go get github.com/vapolia/analytics-clients/clients/go` |

Read the chosen client's README before writing anything: its Summary is the whole integration.

Two clients in the same product is normal — a mobile app and its server each measure what only they
see. Never two in the same process.

## The integration

Start once, track anywhere, expose the opt-out. The mobile clients also own the install id, the
device context and the background flush; do not reimplement any of it.

**No client emits an event of its own**, `first_open` and `app_open` included: a name the client
invented is a name the list may not carry, and it would be dropped in silence. Each one exposes what
you need to place them yourself — a first-run flag, and the lifecycle it already watches in order to
flush (`isFirstRun`/`onForeground` in Kotlin, Swift and JS; `IsFirstRun`/`IAppLifecycle` in .NET;
nothing in Go, which has no lifecycle).

What is true of the *installation* rather than of one event — a plan, a finished tutorial, an
install-age bucket — is the **batch context**: a flat map of scalars the app supplies, keyed by the
context names the operator gave you. It changes while the app runs, so every client asks for it again
at every event.

Two rules that decide most integration bugs:

- `track` never blocks, never throws, never reports an error. There is nothing to `await`, nothing to
  catch, no return value to test. Losses are counted in `stats`.
- Props and context values are scalars only — string ≤64 chars, finite number, bool — 12 each. No
  object, no array, no free-form text.

**Go is the exception**: it has no device, no lifecycle and no id of its own. The app sends its
install id, device and context in the RPC payload and the server forwards them. Never derive the id
from a user id, and never fabricate a device context server-side.

## Counting opens

Three questions come up on every integration. The client gives the signal, the app names the event,
and what is counted from them is decided where the numbers are read.

| Question | What the app sends |
|---|---|
| How many installations opened the app for the first time | `first_open`, once per installation, on the client's first-run flag, followed by `markSeen()`. The .NET client has no `markSeen()`: its `IsFirstRun` turns false once the id is issued. |
| How many openings | `app_open`, at start and on each return to the foreground, from the client's lifecycle hook. |
| What people do first after opening | Nothing new: the actions the app already sends. The first one after an `app_open` is read out of the sequence, not marked at the call site. |

```kotlin
if (Analytics.isFirstRun) { Analytics.track("first_open"); Analytics.markSeen() }
Analytics.track("app_open")
Analytics.onForeground = { Analytics.track("app_open") }
```

The same three lines exist in every client's README, under its own spelling. Emitting `first_open`
without `markSeen()` sends it again at the next launch.

## The welcome popup

Storing an identifier on a device is governed one country at a time, and what a country asks for
falls into four regimes. [OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md) holds the four popups
and the reasoning; what follows is how the app picks between them.

**The detected locale picks the popup, at runtime.** Read the device locale as a BCP-47 tag, map it to
a regime, and show the matching popup. Nothing is hardcoded to one country and nothing asks the person
where they are. The client reads the same tag for `requiresPriorConsent`.

| Platform | Device locale |
|---|---|
| Android | `Locale.getDefault().toLanguageTag()` |
| iOS | `Locale.current.language.languageCode` and `Locale.current.region` |
| Expo | `expo-localization`, `getLocales()[0].languageTag` |
| .NET | `CultureInfo.CurrentUICulture` and `RegionInfo.CurrentRegion` |
| Website | `Accept-Language`. Never the IP address. |

| Regime | Countries | Popup | Client |
|---|---|---|---|
| 1. Exempt | `FR` `IT` `ES` `NL` | Terms, one button | Start normally |
| 2. Consent | The rest of the EEA, `GB`, Quebec | Terms, plus a question with `REFUSE` and `ACCEPT` of equal prominence | `requiresPriorConsent` resolves to true, flipped by the answer |
| 3. Notice | `US` `CA` `BR` `CH` `JP` `AU`, most of the rest of the world | The popup of regime 1 | Start normally |
| 4. Not measured | `KR` | No question: it is never measured | `KR` in `excludedCountries` |

Regime 2 in full: `AT` `BE` `BG` `HR` `CY` `CZ` `DK` `EE` `FI` `DE` `GR` `HU` `IE` `IS` `LI` `LT`
`LU` `LV` `MT` `NO` `PL` `PT` `RO` `SE` `SI` `SK` `GB`.

Every client answers this itself while `requiresPriorConsent` is null, from the device locale as a
BCP-47 tag — `localeRequiresPriorConsent(locale)`, or
`PriorConsentCountries.LocaleRequiresPriorConsent` in .NET. The list is only as current as the version
installed.

**That default must not be taken as a legal basis: the choice stays with the app, and so does the
liability for it.**

Two cases the mapping cannot settle on its own:

- **A tag carrying no region**, `fr` or an unreadable locale, takes regime 2.
- **Quebec** is regime 2 while the rest of Canada is regime 3, and the region setting says `CA` for
  both. `fr-CA` takes regime 2 and `en-CA` regime 3, so a francophone elsewhere in Canada is asked
  needlessly and an anglophone in Quebec is not asked at all. An app that knows the province settles
  it by setting `requiresPriorConsent` itself.

Under regime 2 nothing is sent and no identifier is written until the person has answered:

```kotlin
Analytics.start(this, AnalyticsOptions(
    ingestionUrl = "https://<base>/<source>",
    excludedCountries = setOf("KR"),
    requiresPriorConsent = null,   // the table above, read from the device locale
))

// the popup's two buttons
onAccept = { Analytics.isOptedOut = false }
onRefuse = { Analytics.isOptedOut = true }
```

`requiresPriorConsent` applies only while the person has not answered, so an acceptance survives the next
launch. Calling `start()` before the answer writes nothing to the device and sends nothing; the
acceptance resumes collection with no restart. The same option and the same property exist in Swift,
JS and .NET, where `IsDebugBuild = true` is a build switch rather than the consent gate.

A website serves every regime at once, so it decides per request, from the `Accept-Language` tag. No
identity cookie is written until that visitor accepts. `AnalyticsWebOptions.RequiresPriorConsentForLocale`
is where a site answers that itself.

Both buttons carry the same style, the same size and the same prominence. A refusal placed one level
below the acceptance is what the CNIL fined Google and Facebook for in 2021.

## What you owe, that the library cannot do

[OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md) is the full page. The four items:

1. **Inform** — the privacy policy names the identifier, where it is stored, and for how long. The
   exemption waives consent, not information.
2. **Offer a refusal** — wire the client's opt-out to a visible switch in the settings screen, in
   every regime. This is an obligation; an integration without it voids the exemption.
3. **Never supply your own identifier** — no account id, session id, device id, advertising id, or
   hash of one. The 13-month renewal is the client's job; do not defeat it.
4. **Declare in the stores** in the same release (Apple privacy manifest, Play Data Safety).

Adding personalisation, A/B testing, attribution, or sharing the raw data ends the exemption and
requires a real consent banner for everyone.

## Build token: for an app, once its source requires it

A source that requires one answers `401` to any batch without a valid build token, and that build is
not measured. The token is issued per build and embedded in the app at build time — never generated,
never hardcoded across builds. Add a CI step before the app build:

```bash
# GitHub Actions (permissions: id-token: write). The operator declares the audience.
OIDC=$(curl -sH "Authorization: bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=<base-host>" | jq -r .value)
# Codemagic: OIDC=$ANA_CI_TOKEN, a secret variable the operator issued.
BUILD_TOKEN=$(curl -sf -X POST https://<base>/tokens/<source>/build -H "Authorization: Bearer $OIDC" \
  -H 'Content-Type: application/json' -d "{\"os\":\"android\",\"build\":\"$BUILD_NUMBER\"}" | jq -r .token)
```

Pass it to the client as `token` (Kotlin `AnalyticsOptions`, Swift `AnalyticsOptions`, JS options) or
`Token` (.NET), through the build system: a BuildConfig field, an Info.plist key, `app.config.ts`
extra, an MSBuild property. `os` and `build` must be those of the binary. A website or a server sends
no build token.

The platform, the build, the OS version, the locale and the store come from the build token only.
Without one, a batch carries `country` alone, and the collector measures none of those axes for that build.

## Rate limiting: for a server or a website only

The collector limits per client address. An app never notices. A website or a game server is one
address for all of its users and will be throttled.

The answer is a server token, which the operator issues: the limit then partitions on it. Set it as
`AnalyticsOptions.Token` (.NET) or `Options.APIKey` (Go). Never ship one inside a mobile app — a key
in a binary is extractable, and whoever extracts it spends everyone's quota.

A server that counts what happens on its own side — a wake-up sent, a flag expired — sends those
events **without `installId`**, under its server token. They belong to no installation: never store or
forward a player's install id to fill it in, and never make one up.

## Check it worked

There is no success signal on the wire. `204` covers acceptance and silent rejection alike, so the
client cannot tell a name off the list from a working integration.

What you can see from the app:

- `stats.sent > 0` and `stats.requests > 0` — the batches left.
- `stats.rejected > 0` — the client itself refused them: a name over 64 characters, a non-scalar
  prop, a country in `excludedCountries`.
- `onError` reporting a `401` — the build token is missing, expired or revoked. A `404` — the
  ingestion URL's source segment is wrong.

Everything else is a question for the operator: ask them to confirm the events arrived under the
names you sent. Sent but absent means the names are not on the list.
