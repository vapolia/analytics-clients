# .NET client

Two packages, one client: **`Vapolia.Analytics.Client`** for MAUI, Android, iOS and Windows apps, and
**`Vapolia.Analytics.Client.AspNetCore`** for Blazor and ASP.NET Core servers. The second depends on the
first; a mobile app never carries an ASP.NET Core dependency.

## TL;DR

```csharp
builder.Services.AddAnalytics(o =>
{
    o.Source = "<sourceName>";
    o.Endpoint = "https://analytics.example.com";
});

// a Blazor or ASP.NET Core app also places the middleware, early, before the endpoints
app.UseAnalytics();
```

```csharp
// anywhere, injected
public sealed class GameViewModel(IAnalytics analytics)
{
    public void Finish(Difficulty difficulty) => analytics.Track("game_end", ("difficulty", difficulty));
}
```

That is the whole integration. The client owns the installation id and its 13-month renewal, the
device context, the flush when the app goes to the background, and the opposition switch.

**It emits no event of its own.** An event name belongs to the source's whitelist, and a client that
invented one would send something the collector drops without a word. `first_open` and `app_open` are
yours to name and to place:

```csharp
if (identity.IsFirstRun) analytics.Track("first_open");
analytics.Track("app_open");
lifecycle.Foreground += () => analytics.Track("app_open");
```

`Track` never blocks, never throws, never reports an error. Losses show up in `Stats`.

An app with no container calls `MobileAnalytics.Start(new AnalyticsOptions { Source = "<sourceName>" })` and
`MobileAnalytics.Current.Track(...)` instead — same client, same behaviour.

One thing a server has to add, if it wants the visitor's time zone on its events: an
[`IAnalyticsTimeZone`](#the-time-zone-of-a-visitor), scoped. Mobile already has it.

## Install

```xml
<!-- MAUI, Android, iOS, Windows -->
<PackageReference Include="Vapolia.Analytics.Client" Version="1.0.0" />

<!-- Blazor / ASP.NET Core -->
<PackageReference Include="Vapolia.Analytics.Client.AspNetCore" Version="1.0.0" />
```

TFMs of the app package: `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`, `net10.0-windows`,
plus a plain `net10.0` that carries the core alone — what the ASP.NET Core package builds on. The platform
targets call the platform APIs directly (`SharedPreferences`, `NSUserDefaults`, `UIDevice`,
`Android.OS.Build`), so the package has **no MAUI dependency** and builds with the `android`/`ios`
workloads alone. It works in a MAUI app and in a plain .NET for Android/iOS app.

A MAUI Windows head resolves `net10.0-windows` and gets the app half — including a file-backed
identity store, because an id regenerated at every launch is not an installation id.

## What the client does on its own

| | |
|---|---|
| Installation id | A GUID in the platform preferences (`UserDefaults` on iOS, not the Keychain, which would outlive the installation), renewed after 390 days. |
| First-seen date | Kept **across renewals**: a 13-month-old installation is not a new one. |
| `FirstSeen` | When the installation was first measured, kept across renewals. The client sends nothing from it: `InstallAge.Bucket(firstSeen, now)` is there for *your* context, under a key you whitelisted. |
| `IsFirstRun` | Whether the installation has been seen before, so *your* `first_open` is emitted once and a renewal does not count as a new install. |
| `IAppLifecycle` | Foreground and background, from the one platform subscription the client already holds — Android's rotation guard included. |
| Background | On Android's last activity stopping and on iOS's `DidEnterBackground`, the queue is sent and spooled. |
| Device context | `country` alone, from **`RegionInfo.CurrentRegion`** (the region setting, not the language). The platform and the build come from the build token. |
| Time zone | Each event carries the device offset in minutes east of UTC (`tz`), read at the instant of the event, apart from its UTC `ts`. |

Turn the background flush off with `AutoFlushOnBackground = false`. There is nothing else to turn off:
the client tracks nothing by itself.

## The switches an app actually needs

**Off in DEBUG** — `Enabled = false` registers `NullAnalytics` behind the same interfaces: nothing is
queued, nothing is stored, no `#if` at the call sites. It is also the only way to start without
measuring: left enabled, a missing `Source` or `Endpoint` throws rather than going quiet.

```csharp
builder.Services.AddAnalytics(o =>
{
    o.Source = "<sourceName>";
#if DEBUG
    o.Enabled = false;
#endif
});
```

**The batch context, read again for every event** — what is true of the installation rather than of
one event. It changes while the app runs, and an event must carry the state it was produced under,
so the client asks rather than caches:

```csharp
sealed class GameAnalyticsContext(IUserState state, IInstallIdentityProvider identity) : IAnalyticsContext
{
    public IReadOnlyDictionary<string, object?> GetContext() => new Dictionary<string, object?>
    {
        ["plan"] = state.Plan,
        ["tutorial_done"] = state.TutorialDone,
        // The client keeps the first-seen date across id renewals; naming the bucket is yours.
        ["install_age"] = identity is MobileInstallIdentityProvider { FirstSeen: { } seen }
            ? InstallAge.Bucket(seen, DateTimeOffset.UtcNow)
            : null,
    };
}

builder.Services.AddSingleton<IAnalyticsContext, GameAnalyticsContext>();
```

Every key must be on the source's `context` whitelist in the collector's configuration - a missing key is dropped in silence. 

**Migrating from another implementation** — `SeedInstallId` hands the client the id an app already had, once, when its own store is empty. 

```csharp
o.SeedInstallId = () => new InstallSeed(
    Preferences.Get("analytics_install_id", null!),
    DateTimeOffset.FromUnixTimeMilliseconds(Preferences.Get("analytics_install_id_created", 0L)),
    DateTimeOffset.FromUnixTimeMilliseconds(Preferences.Get("analytics_first_seen", 0L)));
```

It is read only while no id is stored, so leaving the call in place is safe.

**Giving the sender your own HttpClient** — a handler, a proxy, a client from an `IHttpClientFactory` the app already has:

```csharp
o.CreateHttpClient = () => factory.CreateClient("Vapolia.Analytics");
```

A delegate rather than a factory read from the container: `IHttpClientFactory` lives in
`Microsoft.Extensions.Http`, which a MAUI app does not otherwise carry, and this option is one most
apps never set. Unset, the client makes its own with `RequestTimeout`; a client you supply keeps its
own timeout. On the ASP.NET Core side the package registers a named client itself.

**Holding the process during a background flush** — a send started as the app backgrounds is killed
mid-request unless the platform has been told to hold on. The app supplies its own scope; the client
knows no native API:

```csharp
o.BackgroundScope = async name => await backgroundWork.BeginAsync(name); // iOS: beginBackgroundTask
```

**Reporting losses somewhere other than the log** — `OnError` fires next to the log, with
`permanent: true` for what the collector refused for good (a 404, a malformed payload) and
`permanent: false` for what was given up on or dropped by the client's own ceiling. The transport
exception travels with it, so a reporter can group on its type and its stack and filter out the
transient network failures that are not worth an issue:

```csharp
o.OnError = (exception, reason, permanent) =>
{
    if (exception is not null && IsTransientNetwork(exception))
        return;

    if (exception is not null)
        SentrySdk.CaptureException(exception);
    else
        SentrySdk.CaptureMessage($"analytics: {reason}");
};
```

It is null for a loss with no exception behind it — a 4xx, or a saturated window.

## The web side: no script, and why the cookie is the right answer

The events for a website (`platform: "web"`, e.g. `qr_reveal`, `store_badge_click`) are emitted **from
the server**, during the render of a server-rendered Blazor component. Nothing is added to the page,
nothing appears in the visitor's network tab.

Two consequences worth understanding before you ship it:

| | |
|---|---|
| **Install id** | A first-party cookie (`_vau`, HttpOnly, Secure, SameSite=Lax) that the server sets once. Its expiry is **absolute**: 390 days from creation, never refreshed on a later visit — the exemption caps the identifier at 13 months *with no renewal*, so the usual sliding expiry would void it. When the browser drops it, the next visit creates a new one; that is the rotation. |
| **Country** | From `Accept-Language`, which is the visitor's own browser setting. **Never** from IP geolocation — the collector forbids it and stores no IP. The user agent is not parsed either: guessing a device class from it is fingerprinting for a column nobody reads. |
| **Opposition** | A second cookie, `_vau_off`. When it is there, no id is read and none is written — the visitor is simply not measured. Unlike the identifier it **is** refreshed on each visit: extending a refusal serves the person, extending an identifier does not. |

The right of opposition is an obligation, not an option: the exemption holds only if the visitor can
refuse. Inject `IAnalyticsOptOut` in a settings page and call it before the response is written:

```csharp
@inject IAnalyticsOptOut OptOut

<input type="checkbox" checked="@(!OptOut.OptedOut)" @onchange="e => OptOut.SetOptedOut(!(bool)e.Value!)" />
```

On mobile the same interface is injected, or reached through `MobileAnalytics.Identity`.

This only holds for **server-rendered** Blazor. In a WebAssembly app the code runs in the browser: the
call to `analytics.example.com` would be visible in the devtools, and the id would need `localStorage`. An
event triggered by a click in the browser — a QR reveal, a tap on a store badge — has no server render
to ride on; there is no endpoint here that a script could post to yet.

`UseAnalytics()` must run before the response starts — a cookie cannot be set afterwards. If it is too
late, the visit is simply not measured: one missed hit, never a wrong one.

### The time zone of a visitor

An event carries `tz`, the visitor's offset in minutes east of UTC, next to a `ts` that always stays
UTC. On mobile the device answers and there is nothing to do. On a server there is no answer to give:
no HTTP header carries a time zone, and the machine's own offset is the data centre's, not the
visitor's. Sent as such it would read as a real measurement — so when nobody knows, the field is
omitted and the collector stores unknown, never UTC.

What the site knows, the site supplies. Register an `IAnalyticsTimeZone`, **scoped**: `IAnalytics` is
itself scoped, so the implementation is resolved inside the request and can inject whatever identifies
that visitor.

```csharp
sealed class VisitorTimeZone(IHttpContextAccessor accessor) : IAnalyticsTimeZone
{
    public DateTimeOffset? GetLocalTime(DateTimeOffset at)
    {
        // Set by a script: Intl.DateTimeFormat().resolvedOptions().timeZone
        if (accessor.HttpContext?.Request.Cookies["tz"] is not { Length: > 0 } id)
            return null;

        try { return TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(id)); }
        catch (TimeZoneNotFoundException) { return null; }
    }
}

services.AddHttpContextAccessor();
services.AddScoped<IAnalyticsTimeZone, VisitorTimeZone>();
```

The interface hands back an instant, not a number of minutes, and the client reads the offset off it.
Store the **zone** and convert it at the instant of the event, as above: an offset cached in a cookie
is wrong the day DST changes, without the visitor having touched anything. `Date.getTimezoneOffset()`
is the other trap — it counts minutes *west* of UTC, so it needs its sign flipped.

Only the instant of `at` means anything: the offset it arrives with is the machine's, the data centre's
on a server. Convert it, never read its wall clock.

Outside a request — a background job — there is no scope and the answer is null, which is right: a job
is not a visitor. One that works on behalf of someone whose zone is known supplies it the same way.

### Rate limiting: read this before the first deploy

The collector limits **per client address**, and a website is *one address for all its visitors* — the
default is 600 requests per minute per IPv4 address, per pod. Batching is what keeps this reachable (one request per
(install id, device) group, up to 100 events), but one group per visitor means roughly one request per
visitor per flush interval. A site with real traffic will be throttled at the default.

Raise the deployment's rate limit, or lengthen `FlushInterval`. The same caveat applies to the Go
client on a game server.

For a server, the real answer is a **server key**: set `AnalyticsOptions.ApiKey` to a server token
issued by whoever operates the collector (it names the server's own os and build) and the collector
partitions its limit on that key instead of the shared address — up to 6000/min rather than 600. It
grants nothing else, and a key that does not check out is refused with a 401 rather than demoted, so
an expired one shows up at once instead of being discovered through throttling.

The client also does two things about it on its own:

- **It buffers.** `FlushInterval` defaults to 30 s, so a session costs a handful of requests rather
  than one per event — the same interval as the Kotlin, Swift and JS clients.
- **It has its own ceiling, on the app side only.** `MaxEventsPerWindow` (30) caps what one
  installation may emit per minute; past it everything is dropped until the window ends, rather than
  queued for a collector that would throttle the whole address. A full window is logged and reported
  through `OnError` once, so it is not discovered months later in the numbers. `AddAnalytics` sets it
  to **0 (disabled)** on the server, where one process legitimately speaks for every visitor.

## API

| | |
|---|---|
| `AddAnalytics(o => …)` | Registers `IAnalytics`, `IAnalyticsOptOut` and `IInstallIdentityProvider` — on both hosts, under the same name. |
| `UseAnalytics()` | ASP.NET Core only: ensures the identity cookie exists before anything renders. |
| `IAnalytics.Track(name, props?)` | `IReadOnlyDictionary<string, object?>` or `params (string, object?)[]`. Scalars only: string (≤64 chars), finite number, bool — and **enums, stored by name**. Max 12 per event. |
| `IAnalytics.FlushAsync(ct)` | Sends what is queued and waits. |
| `IAnalytics.Stats` | `Accepted` / `Rejected` / `Dropped` / `Sent` / `Requests`. |
| `IAnalyticsContext.GetContext()` | The batch context, asked for on every event. Keys whitelisted per source under `context:`. |
| `IAnalyticsTimeZone.GetLocalTime(utc)` | The instant as the visitor reads it on their own clock, per event; the client keeps its offset. On a server no HTTP header carries the zone, so the site supplies it (a cookie set by a script, for instance). Mobile uses the device's own zone. |
| `InstallAge.Bucket(firstSeen, now)` | `0` / `1-7` / `8-30` / `31-90` / `90+`, for an app that segments on the age of an installation. |
| `IAnalyticsOptOut` | The right of opposition, on both hosts. Off by default; setting it forgets the id. |
| `MobileAnalytics.Start/Current/FlushAsync/StopAsync` | The entry point for an app without a container. |
| `MobileInstallIdentityProvider.Seed(InstallSeed)` | Adopts an existing installation, once. |
| `MobileInstallIdentityProvider.Update(d => …)` | Corrects the detected country, for an app that reads it better than the region setting. |
| `IAppLifecycle` | `Foreground` / `Background`, registered by `AddAnalytics`, or `MobileAnalytics.Lifecycle`. |
| `MobileInstallIdentityProvider.IsFirstRun` | What decides your own `first_open`. |
| `NullAnalytics.Instance` | The no-op, which `Enabled = false` registers for you. Its opt-out still moves, in memory, so a settings switch bound to `IAnalyticsOptOut` is not stuck in a DEBUG build. |

`AnalyticsOptions`: `Source` and `Endpoint` (both required), `Enabled` (true),
`AutoFlushOnBackground` (true), `AccessToken` (the build token), `ExcludedCountries`, `Context`, `SeedInstallId`, `BackgroundScope`, `OnError`,
`FlushInterval` (30 s), `MaxEventsPerWindow` (30, 0 on the server), `RateWindow` (1 min), `BatchSize`
(100, the collector's ceiling), `QueueCapacity` (4000), `MaxAttempts` (3), `RequestTimeout` (10 s),
`CreateHttpClient`, `ApiKey`, `SpoolPath` (null on a server = no spool), `SpoolCapacity` (1000), `CookieName`
(`_vau`), `InstallIdLifetime` (390 days).

## Failure behaviour

Same contract as the other clients:

- **Full queue** — the newest event is dropped, `Track` returns. Counted in `Dropped`.
- **5xx, network error, 429** — retried up to `MaxAttempts`, honouring `Retry-After`, then **kept** for
  the next flush, and reported through `OnError` as transient.
- **404 / 4xx** — permanent (unknown source, malformed payload); dropped without a retry, reported as
  permanent.
- **Events older than 7 days** — dropped before being sent; the collector refuses them anyway.
- **Process going away** — on mobile the client flushes and spools on its own when the app
  backgrounds. On a server the hosted service flushes on shutdown; set `SpoolPath` if you want a
  restart to recover the queue too.

## Build and test

```bash
dotnet build clients/dotnet/Vapolia.Analytics.Client/Vapolia.Analytics.Client.csproj -f net10.0
dotnet build clients/dotnet/Vapolia.Analytics.Client.AspNetCore/Vapolia.Analytics.Client.AspNetCore.csproj
dotnet run --project clients/dotnet/Vapolia.Analytics.Client.Tests/Vapolia.Analytics.Client.Tests.csproj
```

Tests use MTP, like the collector's own suite — `dotnet run`, not `dotnet test`. They target `net10.0`
and cover the sanitizer, the encoder, the spool, the send path, the per-window ceiling, the error
callback and the cookie identity; the platform `#if` branches — the preferences store, the lifecycle
hooks — are not covered by them.

JSON goes through `System.Text.Json` [source
generation](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation)
(`AnalyticsJsonContext`), so the packages stay trim- and AOT-safe: an iOS build is trimmed by default,
and the reflection path would be gone from it — a failure that only shows up on a device.

## Publishing

`Vapolia.Analytics.Client` and `Vapolia.Analytics.Client.AspNetCore` on NuGet, in the same run and at
the same version, with **no stored API key**:
[`dotnet-client-publish.yml`](../../.github/workflows/dotnet-client-publish.yml) asks GitHub for an OIDC token and
nuget.org exchanges it for a key valid one hour (`NuGet/login@v1`, `id-token: write`).

It needs a trusted-publishing policy on nuget.org — profile → Trusted Publishing — pointing at owner
`vapolia`, repository `analytics`, workflow file `dotnet-client-publish.yml`, **one per package id**. On a
private repository a policy stays *pending full activation* for 7 days until the first successful
publish locks it to the repository id.

```bash
# bump <Version> in both csprojs, commit, then
gh release create dotnet-v1.0.1 --title "dotnet client 1.0.1" --notes "..."
```

The workflow refuses to publish if the tag and either `<Version>` disagree, and runs the tests again
before packing — a version is published once and cannot be taken back.

### Pre-releases

A suffixed version is the whole mechanism on nuget.org: it is hidden from the gallery and from
`dotnet add package` unless prereleases are asked for. There is no dist-tag to set.

```bash
gh release create dotnet-v1.1.0-beta.1 --prerelease --title "dotnet client 1.1.0-beta.1" --notes "..."
dotnet add package Vapolia.Analytics.Client --prerelease   # what a consumer runs
```

Both packages carry the same version, so a beta of one is a beta of both. The workflow refuses a
suffixed version published as a release, and a plain one marked as a pre-release: nuget.org is
immutable, so a number spent by mistake cannot be reissued.
