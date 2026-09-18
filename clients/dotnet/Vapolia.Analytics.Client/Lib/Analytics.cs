namespace Vapolia.Analytics.Client;

sealed class Analytics(
    Sender sender,
    IInstallIdentityProvider identity,
    IAnalyticsContext? context = null,
    AnalyticsOptions? options = null,
    IAnalyticsTimeZone? timeZone = null) : IAnalytics
{
    public AnalyticsStats Stats => sender.Stats;

    public void Track(string name, IReadOnlyDictionary<string, object?>? props = null)
    {
        var installId = Clean.InstallId(identity.GetInstallId());
        var eventName = Clean.Text(name, Clean.MaxValueLength);
        var device = identity.GetDevice().Cleaned(options?.ExcludedCountries);

        if (installId is null || eventName is null || device is null)
        {
            sender.Reject();
            return;
        }

        var now = DateTimeOffset.Now;
        var local = timeZone?.GetLocalTime(now);

        sender.Track(new Pending(
            new BatchKey(installId, device, CurrentContext()),
            new Event(eventName, now.ToUniversalTime(), Clean.Props(props), OffsetOf(local))));
    }

    /// <summary>
    /// Minutes east of UTC.
    /// </summary>
    /// <remarks>
    /// Null is stored as unknown, not UTC.
    /// Outside the inhabited range it is a caller bug, and not sent.
    /// </remarks>
    static int? OffsetOf(DateTimeOffset? local)
    {
        if (local is not { } at)
            return null;

        var minutes = (int)at.Offset.TotalMinutes;
        return minutes is >= -12 * 60 and <= 14 * 60 ? minutes : null;
    }

    /// <summary>
    /// The batch context as it stands at this event, canonical so an unchanged context groups into
    /// one batch instead of one per call.
    /// </summary>
    string CurrentContext()
    {
        var current = context?.GetContext() ?? options?.Context?.Invoke();
        var cleaned = Clean.Props(current, Clean.MaxContextKeys);
        return cleaned is null
            ? "{}"
            : System.Text.Json.JsonSerializer.Serialize(cleaned, AnalyticsJsonContext.Default.DictionaryStringPropValue);
    }

    public void Track(string name, params ReadOnlySpan<(string Key, object? Value)> props)
    {
        if (props.Length == 0)
        {
            Track(name, (IReadOnlyDictionary<string, object?>?)null);
            return;
        }

        var map = new Dictionary<string, object?>(props.Length, StringComparer.Ordinal);
        foreach (var (key, value) in props)
            map[key] = value;

        Track(name, map);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
        => sender.FlushAsync(persist: false, cancellationToken);
}