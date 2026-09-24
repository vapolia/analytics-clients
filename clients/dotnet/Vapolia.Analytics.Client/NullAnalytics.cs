namespace Vapolia.Analytics.Client;

/// <summary>
/// A client that measures nothing: what <see cref="AnalyticsOptions.IsDebugBuild"/> set to true
/// registers, and what <c>MobileAnalytics.Current</c> answers before the real one is started.
///
/// It exists so a DEBUG build needs no <c>#if</c> and no second implementation at the call sites.
/// </summary>
public sealed class NullAnalytics : IAnalytics, IMobileInstallContext
{
    /// <summary>The one instance; it holds nothing.</summary>
    public static readonly NullAnalytics Instance = new();

    /// <summary>
    /// Stub: does nothing
    /// </summary>
    public void Track(string name, IReadOnlyDictionary<string, object?>? props = null) { }

    /// <summary>
    /// Stub: does nothing
    /// </summary>
    public void Track(string name, params ReadOnlySpan<(string Key, object? Value)> props) { }

    /// <summary>
    /// Stub: does nothing
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Stub: returns default
    /// </summary>
    public AnalyticsStats Stats => default;

    /// <summary>
    /// Stub: returns true
    /// </summary>
    public bool IsOptedOut { get; set; } = true;

    /// <summary>
    /// Stub: returns null
    /// </summary>
    public string? InstallId => null;

    /// <summary>
    /// Stub: returns null
    /// </summary>
    public string? Country => null;

    /// <summary>
    /// Stub: returns false
    /// </summary>
    public bool? ConsentAnswer => false;
    
    /// <summary>
    /// Stub: returns false
    /// </summary>
    public bool IsFirstRun => false;

    /// <summary>
    /// Stub: returns true
    /// </summary>
    public bool Seed(InstallSeed seed) => true;

    /// <summary>
    /// Stub: returns now
    /// </summary>
    public DateTimeOffset? FirstSeen => DateTimeOffset.Now;
}
