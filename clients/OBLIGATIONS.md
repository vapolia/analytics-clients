# What integrating one of these clients commits you to

For whoever wires a Vapolia analytics client into an app or a site. The clients carry a lot of the
regime, but four things cannot be carried by a library, and they are yours.

The regime is the French consent-exempt audience measurement one (CNIL). The collector operator
holds the legal dossier; this page is the developer-facing summary of what it expects from you.

## TL;DR

1. **Inform.** The exemption waives consent, **not information** (art. 82 LIL). Say in your privacy
   policy that you measure audience, with what identifier, for how long.
2. **Offer a refusal.** Collection is on by default, which the exemption allows — but only if the
   person can turn it off. Every client has an opt-out; wire it to a visible switch.
3. **Renew the identifier, at most every 13 months, with no extension.** The clients do it; do not
   defeat it by supplying your own id.
4. **Declare accurately in the stores**, in the same release as the code.

## Why there is no consent banner

Writing an identifier onto someone's device normally requires consent. Audience measurement is
exempt, provided it stays audience measurement: own use, no cross-site or cross-app tracking, no
personalisation, no A/B testing, no ad or attribution tool, no raw data shared or sold. The moment
one of those appears, the exemption is gone and a real consent banner — for everyone — is required.

Knowing a user is an adult does not restore it: the exemption hangs on the purpose, not the age.

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
| Kotlin | `Analytics.optedOut = true` |
| Swift | `Analytics.optedOut = true` |
| JS / Expo | `await Analytics.setOptedOut(true)` |
| .NET (both) | `IAnalyticsOptOut.SetOptedOut(true)` — injectable, or `MobileAnalytics.Identity` |
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
