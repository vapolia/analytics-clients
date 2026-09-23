using Microsoft.Extensions.Logging;

namespace Vapolia.Analytics.Client;

// Plain net10.0 only: the app half with the same public surface and no behavior, so a library that
// also targets net10.0 compiles without #if. Nothing is measured, stored or subscribed here.

/// <summary>No-op on plain net10.0: the client runs on the platform targets only.</summary>
public static class MobileAnalytics
{
    sealed class NoLifecycle : IAppLifecycle
    {
        public event Action? Foreground { add { } remove { } }
        public event Action? Background { add { } remove { } }
    }

    static readonly NoLifecycle lifecycle = new();

    /// <summary>Always <see cref="NullAnalytics.Instance"/>.</summary>
    public static IAnalytics Current => NullAnalytics.Instance;

    /// <summary>Always null.</summary>
    public static MobileInstallIdentityProvider? Identity => null;

    /// <summary>Never raises an event.</summary>
    public static IAppLifecycle Lifecycle => lifecycle;

    /// <summary>Returns <see cref="NullAnalytics.Instance"/> and starts nothing.</summary>
    public static IAnalytics Start(
        AnalyticsOptions options,
        ILogger? logger = null,
        IAnalyticsContext? context = null,
        HttpClient? httpClient = null,
        IAnalyticsTimeZone? timeZone = null)
        => NullAnalytics.Instance;

    /// <summary>Does nothing.</summary>
    public static Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Does nothing.</summary>
    public static Task StopAsync() => Task.CompletedTask;
}

/// <summary>No-op on plain net10.0: no id, no country, nothing stored.</summary>
public sealed class MobileInstallIdentityProvider : IInstallContext
{
    /// <summary>Keeps nothing from <paramref name="options"/>.</summary>
    public MobileInstallIdentityProvider(AnalyticsOptions options) { }

    /// <summary>Kept in memory only.</summary>
    public bool IsOptedOut { get; set; }

    /// <summary>Always null.</summary>
    public bool? ConsentAnswer => null;

    /// <summary>Always null.</summary>
    public string? InstallId => null;

    /// <summary>Always false.</summary>
    public bool IsFirstRun => false;

    /// <summary>Always false: nothing is adopted.</summary>
    public bool Seed(InstallSeed seed) => false;

    /// <summary>Kept in memory only.</summary>
    public string? Country { get; set; }

    /// <summary>Always null.</summary>
    public DateTimeOffset? FirstSeen => null;
}
