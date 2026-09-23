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
    o.IngestionUrl = new("https://analytics.example.com/<sourceName>");
});
```

Web apps (Blazor / ASP.NET Core):  
Note: if you wants the visitor's time zone on the events (instead of UTC), register a scoped implementation of [`IAnalyticsTimeZone`](#the-time-zone-of-a-visitor).
```c#
builder.Services.AddAnalytics(o =>
{
    o.IngestionUrl = new("https://analytics.example.com/<sourceName>");
});

app.UseAnalytics();
``` 

App without dependency injection:
```c#
MobileAnalytics.Start(new AnalyticsOptions { IngestionUrl = new("https://analytics.example.com/<sourceName>") });
MobileAnalytics.Current.Track(...);
```

The app half works on the platform targets only: `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst` and `net10.0-windows`.
On plain `net10.0`, `MobileAnalytics`, `MobileInstallIdentityProvider` and `IAppLifecycle` exist as no-op stubs, so a library that also targets `net10.0` compiles without `#if`.
`UseAnalytics` and `AddAnalytics` for apps are the exception: a project that also targets plain `net10.0` puts them behind `#if ANDROID || IOS || MACCATALYST || WINDOWS`.

### Quick Usage
```c#
public sealed class GameViewModel(IAnalytics analytics)
{
    public void Finish(Difficulty difficulty) => analytics.Track("game_end", ("difficulty", difficulty));
}
```

```c#
// Sample: tracks app open, with IInstallContext identity and IAppLifecycle lifecycle injected.
// IsFirstRun is on MobileInstallIdentityProvider, the IInstallContext registered for an app.
if (identity is MobileInstallIdentityProvider { IsFirstRun: true })
    analytics.Track("first_open");
analytics.Track("app_open");
lifecycle.Foreground += () => analytics.Track("app_open"); // not raised for the launch itself
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
This flag is only used while the person has not answered, which `MobileInstallIdentityProvider.ConsentAnswer` reports as null.

When RequiresPriorConsent is null, the SDK answers with a built-in default, as a convenience.
**That default must not be taken as a legal basis: the choice stays yours, and so does the liability for it.**

To use your own choice:
```c#
builder.UseAnalytics(o =>
{
    o.IngestionUrl = new("https://analytics.example.com/myapp");
    o.RequiresPriorConsent = YourRequiresPriorConsentFunc(locale); // default is PriorConsentCountries.LocaleRequiresPriorConsent()
});

// show the popup while the question is unanswered
if (install is MobileInstallIdentityProvider { ConsentAnswer: null })
    ShowWelcomePopup();

// the popup's two buttons, on the injected IInstallContext
onAccept = () => install.IsOptedOut = false;
onRefuse = () => install.IsOptedOut = true;
```

With `RequiresPriorConsent` left null, an app that has no popup yet collects nothing in the countries that require prior consent.

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
When the SDK has no id of its own, `SeedInstallId` can give it the id the previous implementation stored.
The SDK asks for it once, when the person is not opted out. The installation then keeps its id, its age and `IsFirstRun = false`.

The keys and the format are those of the previous implementation. This sample reads ISO-8601 round-trip strings from MAUI `Preferences`:

```csharp
o.SeedInstallId = () =>
    Preferences.Get("analytics_install_id", null) is { } id
        ? new InstallSeed(
            id,
            DateTimeOffset.Parse(Preferences.Get("analytics_install_id_created", ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(Preferences.Get("analytics_first_seen", ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
        : null;
```

A date read in the wrong format seeds 1970 or throws. Return null when there is nothing to adopt.

### Use your own HttpClient

```csharp
o.CreateHttpClient = () => factory.CreateClient("name-this-client");
```
If provided, it takes over all other http-related options (like timeout) which are then ignored.


### Reporting losses somewhere other than the log

`AdvancedOptions.OnError` fires next to the log, with:
- `permanent: true` for what the collector refused for good (a 404, a malformed payload) and
- `permanent: false` for what was given up on or dropped by the client's own ceiling. 

```csharp
o.AdvancedOptions.OnError = (exception, reason, permanent) =>
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

### Keep iOS running while the queue is sent

When the app goes to the background, the client sends the queue. iOS suspends the app a few seconds later, possibly mid-request.
`AppOptions.BackgroundScope` opens a scope around each send, and the client disposes it when the send ends.
On iOS, a background task asks the system for the time to finish:

```csharp
#if IOS || MACCATALYST
o.AppOptions.BackgroundScope = name =>
{
    var app = UIKit.UIApplication.SharedApplication;
    nint id = 0;
    id = app.BeginBackgroundTask(name, () => app.EndBackgroundTask(id));
    return Task.FromResult<IAsyncDisposable>(new BackgroundTask(id));
};

sealed class BackgroundTask(nint id) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        UIKit.UIApplication.SharedApplication.EndBackgroundTask(id);
        return ValueTask.CompletedTask;
    }
}
#endif
```

Without it, the events of a send that iOS suspends and then terminates are lost.

## Web: what is stored in the browser

The web client supports server-rendered requests only: static server rendering, Razor Pages, MVC and minimal APIs.
Interactive Blazor components have no `HttpContext`: `Track` records nothing there, `IsOptedOut` cannot be set, and the client logs a warning once.

The events for a website are emitted from the server, during the render of a server-rendered Blazor component.
No client side script is added to the webpage.

Two consequences:

| | |
|---|---|
| **Install id** | A first-party cookie (`_vau`, HttpOnly, Secure, SameSite=Lax) that the server sets once. It expires after 13 months. |
| **Country** | Automatically extracted from `Accept-Language`, never from IP geolocation. |
| **Opposition** | A second cookie, `_vau_off`: `1` opposed, `0` accepted, absent unanswered. Refreshed on each visit. |

Right of opposition: inject `IInstallContext` and set `IsOptedOut` from a server-rendered form post. The events of that visitor still in the queue are discarded.

```razor
@inject IInstallContext Install

<form method="post" @formname="analytics-opt-out" @onsubmit="Save">
    <AntiforgeryToken />
    <label>
        <input type="checkbox" name="Measured" value="true" checked="@(!Install.IsOptedOut)" />
        Measure my visits
    </label>
    <button type="submit">Save</button>
</form>

@code {
    [SupplyParameterFromForm] public bool Measured { get; set; }

    void Save() => Install.IsOptedOut = !Measured;
}
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
| `MobileInstallIdentityProvider.Country` | The detected country. Set it to correct the region setting with a country the app reads better. |
| `MobileInstallIdentityProvider.IsFirstRun` | What decides your own `first_open`. Stays false after an opposition followed by an acceptance. |
| `MobileInstallIdentityProvider.ConsentAnswer` | `true` accepted, `false` refused, `null` not answered yet: whether to show the welcome popup. |


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
