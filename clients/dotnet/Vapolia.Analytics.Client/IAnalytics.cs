namespace Vapolia.Analytics.Client;

/// <summary>
/// Queues events. Never blocks, never throws, never reports an error. <see cref="Stats"/> is where
/// losses show up. The event name and every property key must be on the source's whitelist, which
/// this client cannot know.
/// </summary>
public interface IAnalytics
{
    /// <summary>Queues one event, with the properties the source's whitelist allows.</summary>
    void Track(string name, IReadOnlyDictionary<string, object?>? props = null);

    /// <summary><c>analytics.Track("game_end", ("result", "win"), ("moves", 34))</c></summary>
    void Track(string name, params ReadOnlySpan<(string Key, object? Value)> props);

    /// <summary>Sends what is queued and waits for it.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>What was accepted, refused, lost and sent since the client started.</summary>
    AnalyticsStats Stats { get; }
}

/// <summary>
/// The right of opposition, which the exemption requires every measured product to offer. This is a
/// refusal, not a permission: collection is on by default. Opting out forgets the installation id, so
/// opting back in later cannot resume the same installation.
/// </summary>
public interface IAnalyticsOptOut
{
    /// <summary>Whether this installation, or this visitor, has opposed the measurement.</summary>
    bool OptedOut { get; }

    /// <summary>Records the refusal, or lifts it.</summary>
    void SetOptedOut(bool value);
}

/// <summary>
/// Who the events belong to, and on what. On mobile this is one installation for the life of the
/// process. On a server it is one visitor per request, read from the cookie, which is why it is
/// resolved per call rather than captured once.
/// </summary>
public interface IInstallIdentityProvider
{
    /// <summary>The current installation id, or null when there is none and none may be created.</summary>
    string? GetInstallId();

    /// <summary>The device context for the current caller.</summary>
    Device GetDevice();
}