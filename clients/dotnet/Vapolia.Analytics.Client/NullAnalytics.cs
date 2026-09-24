namespace Vapolia.Analytics.Client;

/// <summary>
/// A client that measures nothing: what <see cref="AnalyticsOptions.IsDebugBuild"/> set to true
/// registers, and what <c>MobileAnalytics.Current</c> answers before the real one is started.
///
/// It exists so a DEBUG build needs no <c>#if</c> and no second implementation at the call sites.
/// </summary>
sealed class NullAnalytics : IAnalytics, IMobileInstallContext
{
    /// <summary>The one instance; it holds nothing.</summary>
    public static readonly NullAnalytics Instance = new();

    /// <inheritdoc/>
    public void Track(string name, IReadOnlyDictionary<string, object?>? props = null) { }

    /// <inheritdoc/>
    public void Track(string name, params ReadOnlySpan<(string Key, object? Value)> props) { }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public AnalyticsStats Stats => default;

    /// <summary>
    /// The refusal an app bound its settings switch to. Nothing is collected either way, but the
    /// switch has to move: a DEBUG build showing a stuck "opposed" is visible to the user.
    ///
    /// Kept in memory only — a client that measures nothing has nowhere to write it.
    /// </summary>
    public bool IsOptedOut { get; set; }

    /// <inheritdoc/>
    public string? InstallId => null;

    /// <inheritdoc/>
    public string? Country => null;

    public bool? ConsentAnswer { get; }
    public bool IsFirstRun { get; }
    public bool Seed(InstallSeed seed)
    {
        throw new NotImplementedException();
    }

    public DateTimeOffset? FirstSeen { get; }
}
