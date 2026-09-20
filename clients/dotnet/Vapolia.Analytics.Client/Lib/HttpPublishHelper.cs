using System.Net;
using System.Text;

namespace Vapolia.Analytics.Client;

/// <summary>One request to POST {endpoint}/{source}.</summary>
sealed class HttpPublishHelper(HttpClient httpClient, Uri url, string? apiKey = null) : IPublishHelper
{
    public async Task<SendResult> PostAsync(string body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            // Only ever a rate-limit ceiling; the collector accepts the batch either way.
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return SendResult.Ok;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var after = response.Headers.RetryAfter?.Delta ?? TimeSpan.Zero;
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
            return SendResult.Retry(TimeSpan.Zero, $"network error: {e.Message}", e);
        }
    }
}