namespace Vapolia.Analytics.Client;

/// <summary>A snapshot of what the client did since it was created.</summary>
/// <param name="Accepted">Queued by Track.</param>
/// <param name="Rejected">Refused by Track: no id, excluded country, unusable event name.</param>
/// <param name="Dropped">Accepted then lost: full queue, or a permanently refused request.</param>
/// <param name="Sent">Events the collector answered 2xx for.</param>
/// <param name="Requests">Requests issued, retries included.</param>
public readonly record struct AnalyticsStats(
    long Accepted,
    long Rejected,
    long Dropped,
    long Sent,
    long Requests);