# What integrating one of these clients commits you to

For whoever wires a Vapolia analytics client into an app or a website.  
The clients carry a lot of the regime, but four things cannot be carried by a library, and they are yours.

The clients are built to the criteria that exempt audience measurement from consent, listed below.
Where a country asks for more, *The welcome popup* says which one and what to show. The collector
operator holds the legal dossier, and this page is the developer-facing summary of what it expects
from you.

## Summary

1. **Inform.** The exemption waives consent, **not information** (art. 82 LIL). Say in your privacy
   policy that you measure audience, with what identifier, for how long.
2. **Offer a refusal.** Collection is on by default, which the exemption allows — but only if the
   person can turn it off. Every client has an opt-out. Wire it to a visible switch.
3. **Renew the identifier, at most every 13 months, with no extension.** The clients do it. Do not
   defeat it by supplying your own id.
4. **Declare accurately in the stores**, in the same release as the code.

## Why there is no consent banner

Writing an identifier onto someone's device normally requires consent. Audience measurement is
exempt, provided it stays audience measurement: own use, no cross-site or cross-app tracking, no
personalisation, no A/B testing, no ad or attribution tool, no raw data shared or sold. The moment
one of those appears, the exemption is gone and a real consent banner — for everyone — is required.

Knowing a user is an adult does not restore it: the exemption hangs on the purpose, not the age.

## The welcome popup

What a country requires is not a property of the EU. Storing an identifier on a device is governed by
article 5(3) of the ePrivacy directive, transposed one country at a time, and only a few authorities
have published criteria exempting audience measurement from consent. Four regimes follow. Pick from
the device region setting, the same value the clients send as `country`.

This list is a starting point for your own counsel, not a legal opinion. Authority positions move.

### 1. Exempt from consent

**France, Italy, Spain, Netherlands.**

Their authorities published criteria under which first-party audience measurement needs no consent:
own use, aggregated statistics, no cross-app or cross-site tracking, nothing shared. The clients meet
them. Information remains mandatory.

```text
┌────────────────────────────────────────────────────────┐
│                  WELCOME FIRST USE                     │
│                                                        │
│ By using this app, you accept our Terms and Conditions │
│ [link to open/view terms].                             │
│                                                        │
│              [ CONTINUE ]                              │
│                                                        │
└────────────────────────────────────────────────────────┘
```

The Terms link has to reach a privacy policy naming the measurement, the identifier and its lifetime.

### 2. Consent required

**The rest of the EEA plus the United Kingdom and Quebec.**

Nothing is sent until the person has answered.

The .NET, Kotlin, Swift and JS clients answer this themselves, from the device locale, whenever
`requiresPriorConsent` is left null — `localeRequiresPriorConsent(locale)`, or
`PriorConsentCountries.LocaleRequiresPriorConsent` in .NET. The list is only as current as the version
you install. `fr-CA` asks first and `en-CA` does not, which covers Quebec by the language: a
francophone elsewhere in Canada is asked needlessly, an anglophone in Quebec is not asked at all.

```text
┌────────────────────────────────────────────────────────┐
│                  WELCOME FIRST USE                     │
│                                                        │
│ By using this app, you accept our Terms and Conditions │
│ [link to open/view terms].                             │
│                                                        │
│ May we measure how you use it, anonymously? You can    │
│ change your answer at any time in Settings.            │
│                                                        │
│              [ REFUSE ]        [ ACCEPT ]              │
│                                                        │
└────────────────────────────────────────────────────────┘
```

Both buttons carry the same style, the same size and the same prominence. A refusal placed one level
below the acceptance — secondary style, link button, extra tap — is what the CNIL fined Google and
Facebook for in 2021.

The question governs the measurement alone. `REFUSE` must not read as refusing the Terms, which is
why it is phrased as a question about the measurement rather than as a statement.

Wire the answer to the client's opt-out, and leave the switch reachable in Settings.

### 3. Notice, no prior consent

**United States, Canada outside Quebec, Brazil, Switzerland, Japan, Australia**, and most of the
rest of the world.

The popup of regime 1 is enough. What these require is notice at collection and a reachable opt-out.

### 4. Not measured at all

**South Korea.** PIPA requires guardian consent below 14, which an app rated 13+ cannot obtain. Pass
`KR` in `excludedCountries`; the collector refuses it as well.

China and Russia raise data-localisation and export questions that this page does not cover. Ask the
collector operator before shipping there.

## Information: the part most often missed

Exempt from consent is not exempt from information. Your privacy policy has to name what you store on
the device:

| Client | What is written, where |
|---|---|
| [Kotlin](kotlin/README.md) | A random UUID in the app's `SharedPreferences`, renewed after 390 days |
| [Swift](swift/README.md) | A random UUID in `UserDefaults` (not the Keychain, which would outlive the install), renewed after 390 days |
| [JS / Expo](js/README.md) | A random UUID in AsyncStorage, renewed after 390 days |
| [.NET mobile](dotnet/README.md) | A random GUID in the platform preferences (a file on Windows), renewed after 390 days |
| [.NET web](dotnet/README.md) | A first-party cookie `_vau`, HttpOnly, **13 months, expiry never refreshed** |
| [Go](go/README.md) | Nothing: the id is supplied by the app that calls it |

On the web that means naming the cookie, its purpose, its lifetime, and the fact that it is
first-party and never shared. A cookie is also where a visitor expects to read about it, so this is
not a formality.

## The refusal

Collection is on by default and the switch must be reachable, free, and must not break anything when
turned on.

| Client | Where |
|---|---|
| Kotlin | `Analytics.isOptedOut = true` |
| Swift | `Analytics.isOptedOut = true` |
| JS / Expo | `await Analytics.setOptedOut(true)` |
| .NET (both) | `IInstallContext.IsOptedOut = true` — injectable, or `MobileAnalytics.Identity` |
| Go | None: it belongs in the app that owns the user interface |

Every one of them forgets the installation id, so opting back in later cannot resume the same
installation. That is deliberate: a refusal that kept the id would only be a pause.

## The identifier

Random, local, renewed at most every 13 months **without extension** — a sliding expiry refreshed on
each visit is exactly what voids the exemption, which is why the web cookie is written once and never
re-issued.

It must never be, or be derived from: an account id, a user id from your game server, a session id, a
device id, an advertising id (IDFA/GAID), or a hash of any of them. The clients cannot check this for
you — the Go client in particular takes whatever id you hand it.

## What the collector guarantees, so you do not have to

- No IP address is ever stored or logged. The country comes from the device or browser locale, never
  from geolocation. The address is read for rate limiting only, as a salted hash whose salt is
  redrawn at every start.
- No account column, and none that could be joined to one.
- Retention capped at 25 months, enforced by dropping partitions.
- A per-source whitelist: an event or property name that is not on it is dropped, silently. Your
  typo is not an error, it is silence.
- Korea is refused server-side as well as in every client (PIPA guardian consent under 14, against a
  13+ rating).

## Server keys are not authentication

A server-to-server caller may present a key (`Authorization: Bearer`) so the rate limit partitions on
it rather than on a client address shared by all its users. It raises a ceiling and grants nothing
else — what may be written is the source whitelist's business, key or no key. A key that does not
check out is refused with a 401 rather than demoted, so an expired one is visible immediately.

Never ship one inside a mobile app: a key in a binary is extractable and whoever extracts it spends everyone's quota.

## Before you ship

- [ ] The privacy policy names the identifier, its lifetime, and the purpose.
- [ ] The opt-out switch exists, is reachable, and is tested.
- [ ] The events you emit are on the source's whitelist in the collector's configuration.
- [ ] Store declarations match the code, in the same release (App Privacy / Data safety).
- [ ] No advertising SDK shares the identifier, and nothing joins it to an account.
