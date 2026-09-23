# .NET client

Nuget Packages:
- `Vapolia.Analytics.Client` for standalone apps (MAUI, Android, iOS, Windows apps)
- `Vapolia.Analytics.Client.AspNetCore` for web apps (Blazor and ASP.NET Core)

## Summary

### Quick Setup
Standalone apps:  
```c#
builder.UseAnalytics(o =>
{
    o.IngestionUrl = "https://analytics.example.com/<sourceName>";
});
```

Web apps (Blazor / ASP.NET Core):  
Note: if you wants the visitor's time zone on the events (instead of UTC), register a scoped implementation of [`IAnalyticsTimeZone`](#the-time-zone-of-a-visitor).
```c#
builder.Services.AddAnalytics(o =>
{
    o.IngestionUrl = "https://analytics.example.com/<sourceName>";
});

app.UseAnalytics();
``` 

App without dependency injection:
```c#
MobileAnalytics.Start(new AnalyticsOptions { IngestionUrl = "https://analytics.example.com/<sourceName>" });
MobileAnalytics.Current.Track(...);
```

### Quick Usage
```c#
public sealed class GameViewModel(IAnalytics analytics)
{
    public void Finish(Difficulty difficulty) => analytics.Track("game_end", ("difficulty", difficulty));
}
```

```c#
// Sample: tracks app open
if (identity.IsFirstRun) analytics.Track("first_open");
analytics.Track("app_open");
lifecycle.Foreground += () => analytics.Track("app_open");
```


## What the client SDK does on its own

| | |
|---|---|
| Installation id | A GUID in the platform preferences, renewed after 13 months. |
| First-seen date | Kept across renewals |
| `FirstSeen` | When the installation was first measured, kept across renewals.  |
| `IsFirstRun` | Whether the installation has been seen before, so *your* `first_open` is emitted once and a renewal does not count as a new install. |
| `IAppLifecycle` | Foreground and background, from the one platform subscription the client already holds — Android's rotation guard included. |
| Background | On Android's last activity stopping and on iOS's `DidEnterBackground`, the queue is sent and spooled. |
| Device context | `country` alone, from **`RegionInfo.CurrentRegion`** (the region setting, not the language). The platform and the build come from the build token. |
| Time zone | Each event carries the device offset in minutes east of UTC (`tz`), read at the instant of the event, apart from its UTC `ts`. |

Turn the background flush off with `FlushesOnBackground = false`.

## What this SDK cannot check for you

- `ExcludedCountries`: pass the list that applies to your app. What is in it is refused here as well as by the collector.
- The privacy policy, the store declarations, the opposition switch, and the list of excluded countries are the app's obligations — see [OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md).

## RequiresPriorConsent and IsOptedOut

Depending on the country consent may need to be given before the analytics SDK can start collecting data. 
This is controlled by the `RequiresPriorConsent` flag.  
Which countries require this prior consent are in [OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md).  
This flag is only used when IsOptedOut is null (ie: the user never chose the consent yet).

When RequiresPriorConsent is null, the SDK answers with a built-in default, as a convenience.
**That default must not be taken as a legal basis: the choice stays yours, and so does the liability for it.**

To use your own choice:
```c#
builder.UseAnalytics(o =>
{
    o.IngestionUrl = new("https://analytics.example.com/myapp");
    o.RequiresPriorConsent = YourRequiresPriorConsentFunc(locale); // default is PriorConsentCountries.LocaleRequiresPriorConsent()
});

// the popup's two buttons, on the injected IInstallContext
onAccept = () => install.IsOptedOut = false;
onRefuse = () => install.IsOptedOut = true;
```

## Options

### Completely Disable Analytics
```c#
builder.UseAnalytics(o =>
{
#if DEBUG
    // definetly disables analytics for the life of the process
    o.IsDebugBuild = true;
#endif
});
```

### Post additional context along each event batch

Register an implementation of `IAnalyticsContext` to add additional context to each event batch.

```csharp
sealed class GameAnalyticsContext(IUserState state, IInstallContext identity) : IAnalyticsContext
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

### Migrating from another implementation 
When the SDK has no id of its own, `SeedInstallId` can give it one it previously had.

```csharp
o.SeedInstallId = () => new InstallSeed(
    Preferences.Get("analytics_install_id", null!),
    DateTimeOffset.FromUnixTimeMilliseconds(Preferences.Get("analytics_install_id_created", 0L)),
    DateTimeOffset.FromUnixTimeMilliseconds(Preferences.Get("analytics_first_seen", 0L)));
```

### Use your own HttpClient

```csharp
o.CreateHttpClient = () => factory.CreateClient("name-this-client");
```
If provided, it takes over all other http-related options (like timeout) which are then ignored.


### Reporting losses somewhere other than the log

`OnError` fires next to the log, with:
- `permanent: true` for what the collector refused for good (a 404, a malformed payload) and
- `permanent: false` for what was given up on or dropped by the client's own ceiling. 

```csharp
o.OnError = (exception, reason, permanent) =>
{
    if (exception is not null && IsTransientNetwork(exception))
        return;

    if (exception is not null)
       //... 
    else
       //... 
};
```

It is null for a loss with no exception behind it like a 4xx or a saturated window.

## Web: what is stored in the browser

This only holds for server-rendered Blazor.

The events for a website are emitted from the server, during the render of a server-rendered Blazor component.
No client side script is added to the webpage.

Two consequences:

| | |
|---|---|
| **Install id** | A first-party cookie (`_vau`, HttpOnly, Secure, SameSite=Lax) that the server sets once. It expires after 13 months. |
| **Country** | Automatically extracted from `Accept-Language`, never from IP geolocation. |
| **Opposition** | A second cookie, `_vau_off`: `1` opposed, `0` accepted, absent unanswered. Refreshed on each visit. |

Right of opposition: Inject `IInstallContext` and set IsOptedOut to true.

```csharp
@inject IInstallContext OptOut

<input type="checkbox" checked="@(!OptOut.IsOptedOut)" @onchange="e => OptOut.IsOptedOut = !(bool)e.Value!" />
```

One site serves every country at once, so the regime is decided per visitor, from the BCP-47 tag read from `Accept-Language`.
A null `RequiresPriorConsent` does this on its own; set`RequiresPriorConsentForLocale` to answer it yourself:

```csharp
builder.Services.AddAnalytics(o =>
{
    o.IngestionUrl = new("https://analytics.example.com/myapp");
    o.WebOptions.RequiresPriorConsentForLocale = locale => MyOwnLookup(locale);
});
```

Until that visitor accepts, no identity cookie is written and nothing is sent. 
Accepting writes `_vau_off=0`, which outranks the default on the next request.

`UseAnalytics()` must run before the response starts — a cookie cannot be set afterwards. 
If it is too late, the visit is simply not measured.

### Web: time zone of visitors

If you need the timezone of visitors, register an `IAnalyticsTimeZone`, **scoped**.

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

### Web: Rate limiting

The collector has limits **per IP address**.  
The default is 600 requests per minute per IPv4 address, per pod.  

A website with real traffic will be throttled by default.  
To avoid this, raise the deployment's rate limit for this kind of source.

For a server, the real answer is a **server key**: `AnalyticsOptions.Token`, a server token issued by the collector
which uses it to throttle instead of the IP address, with a default max events of 6000/min instead of 600. The same
property also carries a build token for a mobile app — the collector tells them apart from the token itself.

### Other

The client also does two things on its own:

- **It buffers events.** `FlushInterval` defaults to 30s.
- Standalone apps only: the SDK limits to `MaxEventsPerWindow` (30) the max number of events per window. Past it everything is dropped until the window ends. 

## API

| Dependency Injection Provider| |
|---|---|
| `IAnalytics.Track(name, props?)` | `IReadOnlyDictionary<string, object?>` or `params (string, object?)[]`. Scalars only: string (≤64 chars), finite number, bool — and **enums, stored by name**. Max 12 per event. |
| `IAnalytics.FlushAsync(ct)` | Sends what is queued and waits. |
| `IAnalytics.Stats` | `Accepted` / `Rejected` / `Dropped` / `Sent` / `Requests`. |
| `IAnalyticsContext.GetContext()` | The batch context, asked for on every event. Keys whitelisted per source under `context:`. |
| `IAnalyticsTimeZone.GetLocalTime(utc)` | The instant as the visitor reads it on their own clock, per event; the client keeps its offset. On a server no HTTP header carries the zone, so the site supplies it (a cookie set by a script, for instance). Mobile uses the device's own zone. |
| `InstallAge.Bucket(firstSeen, now)` | `0` / `1-7` / `8-30` / `31-90` / `90+`, for an app that segments on the age of an installation. |
| `IAppLifecycle` | `Foreground` / `Background`, registered by `AddAnalytics`, or `MobileAnalytics.Lifecycle`. |

| Static provider | |
|---|---|
| `MobileAnalytics.Start/Current/FlushAsync/StopAsync` | The entry point for an app without a container. |
| `MobileInstallIdentityProvider.Seed(InstallSeed)` | Adopts an existing installation, once. |
| `MobileInstallIdentityProvider.Update(d => …)` | Corrects the detected country, for an app that reads it better than the region setting. |
| `MobileInstallIdentityProvider.IsFirstRun` | What decides your own `first_open`. |


## Failure behavior

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

Tests use MTP, use `dotnet run`, not `dotnet test`.

JSON goes through `System.Text.Json` [source generation](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation) (`AnalyticsJsonContext`), so the packages stay trim- and AOT-safe.

## Publishing

A release runs the publish workflow. It needs a trusted-publishing policy on nuget.org.

### Pre-releases

```bash
gh release create dotnet-v1.1.0-beta.1 --prerelease --title "dotnet client 1.1.0-beta.1" --notes "..."
```

### Releases

```bash
gh release create dotnet-v1.0.1 --title "dotnet client 1.0.1" --notes "..."
```
