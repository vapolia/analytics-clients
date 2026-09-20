namespace Vapolia.Analytics.Client;

/// <summary>
/// Utility interface to help with sending data to the server.
/// </summary>
/// <remarks>
/// An interface so the publishing path is testable.
/// </remarks>
interface IPublishHelper
{
    Task<SendResult> PostAsync(string body, CancellationToken cancellationToken);
}

readonly record struct SendResult(SendOutcome Outcome, TimeSpan RetryAfter, string Reason, Exception? Exception = null)
{
    public static readonly SendResult Ok = new(SendOutcome.Ok, TimeSpan.Zero, "");
    public static SendResult Retry(TimeSpan after, string reason, Exception? exception = null)
        => new(SendOutcome.Retry, after, reason, exception);
    public static SendResult Permanent(string reason) => new(SendOutcome.Permanent, TimeSpan.Zero, reason);
}

enum SendOutcome
{
    Ok,
    /// <summary>Worth sending again: a network error, a 5xx, or a 429 with its Retry-After.</summary>
    Retry,
    /// <summary>A retry cannot fix it: unknown source, malformed payload.</summary>
    Permanent,
}
