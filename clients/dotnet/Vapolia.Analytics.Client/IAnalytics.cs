namespace Vapolia.Analytics.Client;

/// <summary>
/// Queues events
/// </summary>
/// <remarks>
/// <see cref="Stats"/> report losses.
/// The event name and every property key must be declared on the source's whitelist on the collector.
/// </remarks>
public interface IAnalytics
{
    /// <summary>Queues one event</summary>
    void Track(string name, IReadOnlyDictionary<string, object?>? props = null);

    /// <summary>
    /// Queues one event
    /// <code>analytics.Track("game_end", ("result", "win"), ("moves", 34))</code>
    /// </summary>
    void Track(string name, params ReadOnlySpan<(string Key, object? Value)> props);

    /// <summary>Sends the remaining items in the queue.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>What was accepted, refused, lost and sent since the client started.</summary>
    AnalyticsStats Stats { get; }
}