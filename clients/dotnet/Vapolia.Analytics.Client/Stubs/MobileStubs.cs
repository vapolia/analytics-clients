using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Vapolia.Analytics.Client;

// Plain net10.0 only: for a library that also targets net10.0 compiles without #if

/// <summary>Not supported, not a platform target.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MobileAnalytics
{
    /// <summary>Not supported, not a platform target.</summary>
    public static IAnalytics Current => throw new NotImplementedException("The build target is not a platform target.");

    /// <summary>Not supported, not a platform target.</summary>
    public static IMobileInstallContext? Identity => throw new NotImplementedException("The build target is not a platform target.");
    
    /// <summary>Not supported, not a platform target.</summary>
    public static IAppLifecycle Lifecycle => throw new NotImplementedException("The build target is not a platform target.");

    /// <summary>Not supported, not a platform target.</summary>
    public static IAnalytics Start(AnalyticsOptions options, ILogger? logger = null, IAnalyticsContext? context = null, HttpClient? httpClient = null, IAnalyticsTimeZone? timeZone = null)
        => throw new NotImplementedException("The build target is not a platform target.");

    /// <summary>Not supported, not a platform target.</summary>
    public static Task FlushAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException("The build target is not a platform target.");

    /// <summary>Not supported, not a platform target.</summary>
    public static Task StopAsync() => throw new NotImplementedException("The build target is not a platform target.");
}

/// <summary>Not supported, not a platform target.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class MobileInstallContext : IMobileInstallContext
{
    /// <summary>Not supported, not a platform target.</summary>
    public MobileInstallContext(AnalyticsOptions options) 
        => throw new NotImplementedException("The build target is not a platform target.");

    /// <summary>Not supported, not a platform target.</summary>
    public bool IsOptedOut { get; set; }

    /// <summary>Not supported, not a platform target.</summary>
    public bool? ConsentAnswer => throw new NotImplementedException("The build target is not a platform target.");

    /// <summary>Not supported, not a platform target.</summary>
    public string? InstallId => throw new NotImplementedException("The build target is not a platform target.");

    /// <summary>Not supported, not a platform target.</summary>
    public bool IsFirstRun => throw new NotImplementedException("The build target is not a platform target.");

    /// <summary>Not supported, not a platform target.</summary>
    public bool Seed(InstallSeed seed) => throw new NotImplementedException("The build target is not a platform target.");

    /// <summary>Not supported, not a platform target.</summary>
    public string? Country { get; set; }

    /// <summary>Not supported, not a platform target.</summary>
    public DateTimeOffset? FirstSeen => throw new NotImplementedException("The build target is not a platform target.");
}
