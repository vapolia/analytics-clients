namespace Vapolia.Analytics.Client;

/// <summary>
/// Uncommon Apps-only options
/// </summary>
public sealed class AnalyticsAppOptions
{
    /// <summary>
    /// Whether the client flushes and spools when the app goes to the background — the last moment iOS guarantees the process runs.
    /// Mobile only; a server flushes when the host stops.
    /// </summary>
    public bool FlushesOnBackground { get; set; } = true;

    /// <summary>
    /// A scope that prevents the app from being killed mid-send by the host.
    /// Mostly used in mobile apps.
    /// </summary>
    public Func<string, Task<IAsyncDisposable>>? BackgroundScope { get; set; }
}