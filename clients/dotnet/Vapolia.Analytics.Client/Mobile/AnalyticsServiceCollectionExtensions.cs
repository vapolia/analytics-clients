using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vapolia.Analytics.Client;

/// <summary>Wires the client into an app that has a container — MAUI, or a native app with one.</summary>
public static class AnalyticsServiceCollectionExtensions
{
    /// <summary>
    /// Configure and enable the collection of analytics events.
    /// </summary>
    /// <remarks>
    /// Registers <see cref="IAnalytics"/>, <see cref="IAnalyticsOptOut"/> and <see cref="IInstallIdentityProvider"/>. 
    /// If <see cref="AnalyticsOptions.Enabled"/> is false or if the source is missing, it registers <see cref="NullAnalytics"/>.
    ///
    /// <see cref="IAnalyticsContext"/> is queried once when posting a batch of events.
    /// To use your own HttpClient, set <see cref="AnalyticsOptions.CreateHttpClient"/>.
    /// You can hook into analytics lifecycle events by injecting <see cref="IAppLifecycle"/> and/or <see cref="IInstallIdentityProvider"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddAnalytics(o => o.Source = "&lt;sourceName&gt;");
    /// </code>
    /// </example>
    public static IServiceCollection AddAnalytics(this IServiceCollection services, Action<AnalyticsOptions> configure)
    {
        services.AddOptions<AnalyticsOptions>().Configure(configure);

        services.TryAddSingleton<IAnalytics>(provider => MobileAnalytics.Start(
            provider.GetRequiredService<IOptions<AnalyticsOptions>>().Value,
            provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(AnalyticsServiceCollectionExtensions).Namespace!),
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
