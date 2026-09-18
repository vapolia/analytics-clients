using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vapolia.Analytics.Client;

/// <summary>Wires the client into an ASP.NET Core or Blazor app.</summary>
public static class AnalyticsServiceCollectionExtensions
{
    /// <summary>
    /// The whole integration, the same call a mobile app makes:
    ///
    /// <code>
    /// builder.Services.AddAnalytics(o => o.Source = "&lt;sourceName&gt;");
    /// ...
    /// app.UseAnalytics();
    /// </code>
    ///
    /// Then inject <see cref="IAnalytics"/> and call <c>Track</c>. The send happens on the server, so
    /// nothing appears in the visitor's network tab — which only holds for server-rendered Blazor.
    ///
    /// With <see cref="AnalyticsOptions.Enabled"/> false, or no source, what is registered is
    /// <see cref="NullAnalytics"/>: the same call sites, nothing collected, no <c>#if</c> here.
    /// </summary>
    public static IServiceCollection AddAnalytics(this IServiceCollection services, Action<AnalyticsOptions> configure)
    {
        services.AddOptions<AnalyticsOptions>()
            // A server speaks for every visitor, so the per-client ceiling that protects a phone from
            // itself would throttle a whole site here. Runs before the caller's own configuration,
            // which can still set it back.
            .Configure(o => o.MaxEventsPerWindow = 0)
            .Configure(configure)
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Source), "AnalyticsOptions.Source is required")
            .Validate(o => !o.Enabled || Uri.TryCreate(o.Endpoint, UriKind.Absolute, out _),
                "AnalyticsOptions.Endpoint is required: the collector's absolute base URL, e.g. \"https://analytics.example.com\"");

        services.AddHttpContextAccessor();
        services.TryAddScoped<CookieInstallIdentityProvider>();
        services.TryAddScoped<IInstallIdentityProvider>(p => Enabled(p)
            ? p.GetRequiredService<CookieInstallIdentityProvider>()
            : NullAnalytics.Instance);
        // Injected by a settings page to offer the refusal: the same scoped instance, so what it
        // writes is what the identity reads.
        services.TryAddScoped<IAnalyticsOptOut>(p => Enabled(p)
            ? p.GetRequiredService<CookieInstallIdentityProvider>()
            : NullAnalytics.Instance);

        services.AddHttpClient(HttpClientName, (provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<AnalyticsOptions>>().Value;
            http.Timeout = options.RequestTimeout;
        });

        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AnalyticsOptions>>().Value;
            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            var url = $"{options.Endpoint.TrimEnd('/')}/{options.Source.Trim('/')}";

            return new Sender(
                options,
                new HttpPoster(http, url, options.ApiKey),
                options.SpoolPath is { Length: > 0 } path ? new Spool(path, options.SpoolCapacity) : null,
                provider.GetRequiredService<ILogger<IAnalytics>>());
        });

        services.TryAddScoped<IAnalytics>(provider => Enabled(provider)
            ? new Analytics(
                provider.GetRequiredService<Sender>(),
                provider.GetRequiredService<IInstallIdentityProvider>(),
                provider.GetService<IAnalyticsContext>(),
                provider.GetRequiredService<IOptions<AnalyticsOptions>>().Value,
                provider.GetService<IAnalyticsTimeZone>())
            : NullAnalytics.Instance);

        services.AddHostedService<AnalyticsHostedService>();
        return services;
    }

    /// <summary>
    /// Ensures the identity cookie exists before anything renders. Place it early in the pipeline —
    /// after <c>UseRouting</c> is fine, but before the endpoints: once the response has started, no
    /// cookie can be set and the visit goes unmeasured.
    /// </summary>
    public static IApplicationBuilder UseAnalytics(this IApplicationBuilder app)
        => app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            context.RequestServices.GetRequiredService<IInstallIdentityProvider>().GetInstallId();
            await next(context);
        });

    static bool Enabled(IServiceProvider provider)
        => provider.GetRequiredService<IOptions<AnalyticsOptions>>().Value is
            { Enabled: true, Source.Length: > 0, Endpoint.Length: > 0 };

    internal const string HttpClientName = "vapolia.analytics";
}

/// <summary>
/// Flushes what is queued when the host stops, so a deploy does not drop the last minute.
///
/// The sender is resolved rather than injected: a disabled client never builds one, and asking for
/// it here would start a background loop for a site that measures nothing.
/// </summary>
sealed class AnalyticsHostedService(IServiceProvider provider, IOptions<AnalyticsOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return;

        var sender = provider.GetRequiredService<Sender>();
        await sender.FlushAsync(persist: true, cancellationToken).ConfigureAwait(false);
        await sender.DisposeAsync().ConfigureAwait(false);
    }
}
