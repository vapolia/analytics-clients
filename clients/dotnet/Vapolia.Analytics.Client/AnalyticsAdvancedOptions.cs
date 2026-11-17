using Microsoft.Extensions.Logging;

namespace Vapolia.Analytics.Client;

/// <summary>
/// Uncommon options
/// </summary>
public sealed class AnalyticsAdvancedOptions
{
    /// <summary>How long events are buffered before they go out.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Max events accepted per <see cref="RateWindow"/>.
    /// Beyond it, everything is dropped until the window ends.
    /// Set to zero to disable (default for server to server).
    /// </summary>
    /// <remarks>
    /// That keeps a buggy client from getting throttled by the collector.
    /// </remarks>
    public int MaxEventsPerWindow { get; set; } = 30;

    /// <summary>The window <see cref="MaxEventsPerWindow"/> counts in.</summary>
    /// <remarks>fixed window, not sliding</remarks>
    public TimeSpan RateWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Max events to send per batch</summary>
    public int BatchSize { get; set; } = Clean.MaxEventsPerBatch;

    /// <summary>Max events that can wait to be sent. Beyond it Track() drops everything.</summary>
    public int QueueCapacity { get; set; } = 4000;

    /// <summary>
    /// Max attempts to send a batch during one flush. Events still unsent after that are kept for the next
    /// flush, until they are 7 days old or the queue is full.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Timeout when posting analytics data to the collector.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Events can be written/restored to/from disk, before shutdown/after restore, to prevent losing them.
    /// </summary>
    /// <remarks>
    /// A null disables the spool (suggested for webservers where a restart is rare).
    /// </remarks>
    public string? SpoolPath { get; set; }

    /// <summary>Events kept in the spool file.</summary>
    public int SpoolCapacity { get; set; } = 1000;

    /// <summary>
    /// 13 months minus a margin for clock drift. Both the cookie and the mobile identity use it.
    /// </summary>
    public TimeSpan InstallIdLifetime { get; set; } = TimeSpan.FromDays(390);

    /// <summary>
    /// How long a refusal is remembered. Unlike the identifier, it is refreshed on every visit.
    /// </summary>
    public TimeSpan OptOutLifetime { get; set; } = TimeSpan.FromDays(390);

    /// <summary>
    /// The HttpClient the sender uses, or null to use the default.
    /// </summary>
    public Func<HttpClient>? CreateHttpClient { get; set; }

    /// <summary>
    /// Called on every loss, next to the log. Can be used to notify the user that an issue is ongoing.
    /// </summary>
    public AnalyticsErrorHandler? OnError { get; set; }

    /// <summary>Replaces every value outside its range by the nearest bound or the default, and logs each one.</summary>
    internal void Normalize(ILogger? logger)
    {
        var defaults = new AnalyticsAdvancedOptions();

        if (FlushInterval <= TimeSpan.Zero || FlushInterval > MaxFlushInterval)
            FlushInterval = Replaced(nameof(FlushInterval), FlushInterval, $"]0, {MaxFlushInterval}]", defaults.FlushInterval);
        if (RateWindow <= TimeSpan.Zero)
            RateWindow = Replaced(nameof(RateWindow), RateWindow, "> 0", defaults.RateWindow);
        if (RequestTimeout <= TimeSpan.Zero)
            RequestTimeout = Replaced(nameof(RequestTimeout), RequestTimeout, "> 0", defaults.RequestTimeout);
        if (BatchSize is < 1 or > Clean.MaxEventsPerBatch)
            BatchSize = Replaced(nameof(BatchSize), BatchSize, $"[1, {Clean.MaxEventsPerBatch}]", Math.Clamp(BatchSize, 1, Clean.MaxEventsPerBatch));
        if (QueueCapacity < 1)
            QueueCapacity = Replaced(nameof(QueueCapacity), QueueCapacity, ">= 1", defaults.QueueCapacity);
        if (MaxAttempts < 1)
            MaxAttempts = Replaced(nameof(MaxAttempts), MaxAttempts, ">= 1", 1);
        if (SpoolCapacity < 0)
            SpoolCapacity = Replaced(nameof(SpoolCapacity), SpoolCapacity, ">= 0", 0);

        T Replaced<T>(string name, T value, string range, T used)
        {
            logger?.LogWarning("analytics: {Option} = {Value} is outside {Range}, using {Used}", name, value, range, used);
            return used;
        }
    }

    // The ceiling of a PeriodicTimer period is just under 50 days.
    static readonly TimeSpan MaxFlushInterval = TimeSpan.FromDays(30);
}

/// <summary>Receives every loss reported by the client.</summary>
/// <param name="exception">The transport exception, or null when the loss has none.</param>
/// <param name="reason">What happened, in a sentence.</param>
/// <param name="permanent">True when the events are dropped, false when they are kept for the next flush.</param>
public delegate void AnalyticsErrorHandler(Exception? exception, string reason, bool permanent);