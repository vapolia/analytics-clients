using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vapolia.Analytics.Client;

/// <summary>Wires the client into an ASP.NET Core or Blazor app.</summary>
public static class AnalyticsServiceCollectionExtensions
{
    const string HttpClientName = "vapolia.analytics";
    
    /// <summary>
    /// <code>
    /// builder.Services.AddAnalytics(o => o.IngestionUrl = new("https://analytics.example.com/&lt;sourceName&gt;"));
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
            .Configure(o => o.AdvancedOptions.MaxEventsPerWindow = 0)
            .Configure(configure)
            .Validate(o => !o.Enabled || o.IngestionUrl.IsAbsoluteUri, $"{nameof(AnalyticsOptions)}.{nameof(AnalyticsOptions.IngestionUrl)} is required and must be an absolute URL");

        services.AddHttpContextAccessor();
        services.TryAddScoped<CookieInstallIdentityProvider>();
        services.TryAddScoped<IInstallContext>(p => Enabled(p)
            ? p.GetRequiredService<CookieInstallIdentityProvider>()
            : NullAnalytics.Instance);

        services.AddHttpClient(HttpClientName, (provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<AnalyticsOptions>>().Value;
            http.Timeout = options.AdvancedOptions.RequestTimeout;
        });

        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AnalyticsOptions>>().Value;
            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);

            return new Sender(
                options,
                new HttpPublishHelper(http, options.IngestionUrl, options.Token),
                options.AdvancedOptions.SpoolPath is { Length: > 0 } path ? 
                    new PersistPendingItemsToLocalStorageHelper(path, options.AdvancedOptions.SpoolCapacity, provider.GetService<ILoggerFactory>()?.CreateLogger<PersistPendingItemsToLocalStorageHelper>()) 
                    : null,
                provider.GetRequiredService<ILogger<IAnalytics>>());
        });

        services.TryAddScoped<IAnalytics>(provider => Enabled(provider)
            ? new Analytics(
                provider.GetRequiredService<Sender>(),
                provider.GetRequiredService<IInstallContext>(),
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
        => app.Use(async (context, next) =>
        {
            context.RequestServices.GetRequiredService<IInstallContext>().GetInstallId();
            await next(context);
        });

    static bool Enabled(IServiceProvider provider)
        => provider.GetRequiredService<IOptions<AnalyticsOptions>>().Value is { Enabled: true, IngestionUrl.IsAbsoluteUri: true };
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
