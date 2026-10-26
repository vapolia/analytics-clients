namespace Vapolia.Analytics.Client;

sealed class Analytics(
    Sender sender,
    IInstallContext identity,
    IAnalyticsContext? context = null,
    AnalyticsOptions? options = null,
    IAnalyticsTimeZone? timeZone = null,
    bool holdsUnanswered = false) : IAnalytics
{
    /// <summary>
    /// What was tracked while the consent question is unanswered, in memory only: nothing is written
    /// and nothing is sent before an acceptance, which sends it through <see cref="Answered"/>. A
    /// refusal or the end of the process drops it. An app only, never a website, whose identity is
    /// per visitor.
    /// </summary>
    readonly List<(string Name, IReadOnlyDictionary<string, object?>? Props, string Context, DateTimeOffset At, int? Tz)>? awaiting
        = holdsUnanswered ? [] : null;

    public AnalyticsStats Stats => sender.Stats;

    public void Track(string name, IReadOnlyDictionary<string, object?>? props = null)
    {
        var now = DateTimeOffset.UtcNow;
        var tz = OffsetOf(timeZone?.GetLocalTime(now));

        if (awaiting is not null && identity.ConsentAnswer is null && identity.IsOptedOut)
        {
            lock (awaiting)
                if (awaiting.Count < Clean.MaxEventsPerBatch)
                    awaiting.Add((name, props, CurrentContext(), now, tz));
            return;
        }

        Send(name, props, CurrentContext(), now, tz);
    }

    /// <summary>The answer to an unanswered question: an acceptance sends what was held, a refusal drops it.</summary>
    internal void Answered(bool accepted)
    {
        if (awaiting is null)
            return;

        (string Name, IReadOnlyDictionary<string, object?>? Props, string Context, DateTimeOffset At, int? Tz)[] held;
        lock (awaiting)
        {
            held = [.. awaiting];
            awaiting.Clear();
        }

        if (accepted)
            foreach (var e in held)
                Send(e.Name, e.Props, e.Context, e.At, e.Tz);
    }

    void Send(string name, IReadOnlyDictionary<string, object?>? props, string batchContext, DateTimeOffset at, int? tz)
    {
        var installId = Clean.InstallId(identity.InstallId);
        var eventName = Clean.Text(name, Clean.MaxValueLength);
        var device = new Device { Country = identity.Country }.Cleaned(options?.ExcludedCountries);

        if (installId is null || eventName.IsEmpty || device is null)
        {
            sender.Reject();
            return;
        }

        sender.Track(new Pending(
            new BatchKey(installId, device, batchContext),
            new Event(eventName.ToString(), at, Clean.Props(props), tz)));
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
        if (cleaned is null)
            return "{}";

        // Sorted by key: the same context built in another order must give the same text.
        var sorted = new Dictionary<string, PropValue>(cleaned.OrderBy(p => p.Key, StringComparer.Ordinal), StringComparer.Ordinal);
        return System.Text.Json.JsonSerializer.Serialize(sorted, AnalyticsJsonContext.Default.DictionaryStringPropValue);
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