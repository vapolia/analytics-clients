using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vapolia.Analytics.Client;

/// <summary>Wires the client into an app that has a container — MAUI, or a native app with one.</summary>
public static class AnalyticsServiceCollectionExtensions
{
    /// <summary>
    /// The whole integration:
    ///
    /// <code>
    /// builder.Services.AddAnalytics(o => o.Source = "&lt;sourceName&gt;");
    /// </code>
    ///
    /// Registers <see cref="IAnalytics"/>, <see cref="IAnalyticsOptOut"/> and
    /// <see cref="IInstallIdentityProvider"/>. The client starts the first time one of them is
    /// resolved.
    ///
    /// With <see cref="AnalyticsOptions.Enabled"/> false, or no source, what is registered is
    /// <see cref="NullAnalytics"/>: the same call sites, nothing collected, no <c>#if</c> here.
    ///
    /// An <see cref="IAnalyticsContext"/> in the container is picked up and asked for the batch
    /// context on every event. To give the sender your own HttpClient — from an IHttpClientFactory or
    /// anywhere else — set <see cref="AnalyticsOptions.CreateHttpClient"/>.
    ///
    /// Nothing is tracked for you: inject <see cref="IAppLifecycle"/> and
    /// <see cref="IInstallIdentityProvider"/> and name your own opens.
    /// </summary>
    public static IServiceCollection AddAnalytics(this IServiceCollection services, Action<AnalyticsOptions> configure)
    {
        services.AddOptions<AnalyticsOptions>().Configure(configure);

        services.TryAddSingleton<IAnalytics>(provider => MobileAnalytics.Start(
            provider.GetRequiredService<IOptions<AnalyticsOptions>>().Value,
            provider.GetService<ILoggerFactory>()?.CreateLogger("Vapolia.Analytics.Client"),
            provider.GetService<IAnalyticsContext>(),
            timeZone: provider.GetService<IAnalyticsTimeZone>()));

        // Resolving the client is what starts it; the identity is whatever that produced.
        services.TryAddSingleton<IInstallIdentityProvider>(provider => (IInstallIdentityProvider?)Started(provider) ?? NullAnalytics.Instance);

        services.TryAddSingleton<IAnalyticsOptOut>(provider => (IAnalyticsOptOut?)Started(provider) ?? NullAnalytics.Instance);

        // What an app subscribes to in order to name its own opens, without re-subscribing to the
        // platform the client is already watching.
        services.TryAddSingleton(MobileAnalytics.Lifecycle);

        return services;

        static MobileInstallIdentityProvider? Started(IServiceProvider provider)
        {
            provider.GetRequiredService<IAnalytics>();
            return MobileAnalytics.Identity;
        }
    }
}
