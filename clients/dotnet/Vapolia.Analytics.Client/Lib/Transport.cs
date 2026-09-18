using System.Net;
using System.Text;

namespace Vapolia.Analytics.Client;

enum SendOutcome
{
    Ok,
    /// <summary>Worth sending again: a network error, a 5xx, or a 429 with its Retry-After.</summary>
    Retry,
    /// <summary>A retry cannot fix it: unknown source, malformed payload.</summary>
    Permanent,
}

readonly record struct SendResult(SendOutcome Outcome, TimeSpan RetryAfter, string Reason, Exception? Exception = null)
{
    public static readonly SendResult Ok = new(SendOutcome.Ok, TimeSpan.Zero, "");
    public static SendResult Retry(TimeSpan after, string reason, Exception? exception = null)
        => new(SendOutcome.Retry, after, reason, exception);
    public static SendResult Permanent(string reason) => new(SendOutcome.Permanent, TimeSpan.Zero, reason);
}

/// <summary>What the sender needs from the network. An interface so the send path is testable.</summary>
interface IPoster
{
    Task<SendResult> PostAsync(string body, CancellationToken cancellationToken);
}

/// <summary>One request to POST {endpoint}/{source}.</summary>
sealed class HttpPoster(HttpClient http, string url, string? apiKey = null) : IPoster
{
    public async Task<SendResult> PostAsync(string body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            // Only ever a rate-limit ceiling; the collector accepts the batch either way.
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return SendResult.Ok;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var after = response.Headers.RetryAfter?.Delta
                            ?? TimeSpan.Zero;
                return SendResult.Retry(after, "rate limited (429)");
            }

            // 404 means this source is not configured there, 400 that the payload is not accepted.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return SendResult.Permanent("unknown source (404)");

            if ((int)response.StatusCode < 500)
                return SendResult.Permanent($"refused with {(int)response.StatusCode}");

            return SendResult.Retry(TimeSpan.Zero, $"collector answered {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The exception travels with the result: a reporting callback groups on its type and its
            // stack, and a message string would make every failure its own issue.
            return SendResult.Retry(TimeSpan.Zero, $"network error: {e.Message}", e);
        }
    }
}
