namespace Vapolia.Analytics.Client;

/// <summary>
/// A client that measures nothing: what <see cref="AnalyticsOptions.Enabled"/> set to false
/// registers, and what <c>MobileAnalytics.Current</c> answers before the real one is started.
///
/// It exists so a DEBUG build, or a build with no endpoint configured, needs no <c>#if</c> and no
/// second implementation at the call sites.
/// </summary>
public sealed class NullAnalytics : IAnalytics, IAnalyticsOptOut, IInstallIdentityProvider
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
    public bool OptedOut { get; private set; }

    /// <inheritdoc/>
    public void SetOptedOut(bool value) => OptedOut = value;

    /// <inheritdoc/>
    public string? GetInstallId() => null;

    /// <inheritdoc/>
    public Device GetDevice() => new();
}
