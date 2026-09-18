namespace Vapolia.Analytics.Client;

/// <summary>
/// What is true of the installation for a whole batch, rather than of one event — a plan, a finished
/// tutorial, an install-age bucket. Asked for again on every event rather than captured once: these
/// change while the app runs, and an event must carry the state it was produced under.
///
/// Plain keys and scalars, and nothing the client names itself: which axes an app segments on belong
/// to the app, and every key must be on its source's <c>context</c> whitelist or the collector drops
/// it in silence.
///
/// Register one implementation and the client resolves it; an app without a container sets
/// <see cref="AnalyticsOptions.Context"/> instead.
/// </summary>
public interface IAnalyticsContext
{
    /// <summary>
    /// The context as it stands now. Null or empty sends none. Scalars only: string (≤64 chars),
    /// finite number, bool — and enums, stored by name.
    /// </summary>
    IReadOnlyDictionary<string, object?>? GetContext();
}

/// <summary>
/// An installation this client did not create: the identity of an app measured by something else
/// before, handed over so the switch does not reissue every id at once.
/// </summary>
/// <param name="InstallId">The existing id, in canonical UUID form. Anything else is ignored.</param>
/// <param name="IssuedAt">When that id was created — what the 13-month renewal counts from.</param>
/// <param name="FirstSeen">
/// When the installation was first measured, which survives every renewal: without it a 13-month-old
/// installation looks new again, and an install-age bucket names no one.
/// </param>
public sealed record InstallSeed(string InstallId, DateTimeOffset IssuedAt, DateTimeOffset FirstSeen);

/// <summary>
/// The age of an installation, in buckets, for an app that wants to segment on it.
///
/// The client keeps the first-seen date — it is the only thing that knows it, and it keeps it across
/// id renewals — but it does not send a bucket on its own: that would be the client naming what is
/// measured. Call this from your <see cref="IAnalyticsContext"/> if the key is on your whitelist.
/// </summary>
public static class InstallAge
{
    /// <summary>"0", "1-7", "8-30", "31-90" or "90+".</summary>
    public static string Bucket(DateTimeOffset firstSeen, DateTimeOffset now)
        => (now - firstSeen).TotalDays switch
        {
            < 1 => "0",
            < 8 => "1-7",
            < 31 => "8-30",
            < 91 => "31-90",
            _ => "90+",
        };
}

/// <summary>
/// The time zone of whoever the event is about, asked for at every event. A server has no time zone of its visitors on its own —
/// no HTTP header carries one — so knowing it is the site's business, from a cookie a script set for instance. Register this,
/// scoped, to hand it over. On mobile the client supplies the device's own zone.
/// </summary>
public interface IAnalyticsTimeZone
{
    /// <summary>
    /// The instant <paramref name="at"/> as that person reads it on their own clock, or null when unknown. A zone is converted
    /// rather than an offset computed, and the client keeps the offset of what comes back: an instant has exactly one local
    /// reading, while a wall-clock time falls twice on the day DST ends.
    ///
    /// Only the instant carries meaning here — the offset <paramref name="at"/> arrives with is the calling machine's, which on a
    /// server is the data centre's. Convert it (<c>TimeZoneInfo.ConvertTime</c>, <c>ToOffset</c>), never read its wall clock.
    /// </summary>
    DateTimeOffset? GetLocalTime(DateTimeOffset at);
}
