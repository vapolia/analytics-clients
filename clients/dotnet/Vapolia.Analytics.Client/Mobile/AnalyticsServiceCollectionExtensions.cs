using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
    /// Registers <see cref="IAnalytics"/>, <see cref="IInstallContext"/>.
    /// If <see cref="AnalyticsOptions.Enabled"/> is false or if the source is missing, it registers <see cref="NullAnalytics"/>.
    ///
    /// <see cref="IAnalyticsContext"/> is queried once when posting a batch of events.
    /// To use your own HttpClient, set <see cref="AnalyticsOptions.CreateHttpClient"/>.
    /// You can hook into analytics lifecycle events by injecting <see cref="IAppLifecycle"/> and/or <see cref="IInstallContext"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.UseAnalytics(o => o.Source = "&lt;sourceName&gt;");
    /// </code>
    /// </example>
    public static IHostApplicationBuilder UseAnalytics(this IHostApplicationBuilder builder, Action<AnalyticsOptions> configure)
    {
        builder.Services.AddAnalytics(configure);
        return builder;
    }

    /// <summary>
    /// Same registration as <see cref="UseAnalytics(IHostApplicationBuilder, Action{AnalyticsOptions})"/>,
    /// for an app that only has an <see cref="IServiceCollection"/> to hand — no <see cref="IHostApplicationBuilder"/>.
    /// </summary>
    public static IServiceCollection AddAnalytics(this IServiceCollection services, Action<AnalyticsOptions> configure)
    {
        services.AddOptions<AnalyticsOptions>().Configure(configure);

        services.TryAddSingleton<IAnalytics>(provider => MobileAnalytics.Start(
            provider.GetRequiredService<IOptions<AnalyticsOptions>>().Value,
            provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(AnalyticsServiceCollectionExtensions).Namespace!),
            provider.GetService<IAnalyticsContext>(),
            timeZone: provider.GetService<IAnalyticsTimeZone>()));

        services.TryAddSingleton<IInstallContext>(provider => (IInstallContext?)Started(provider) ?? NullAnalytics.Instance);
        services.TryAddSingleton(MobileAnalytics.Lifecycle);

        return services;

        static MobileInstallIdentityProvider? Started(IServiceProvider provider)
        {
            provider.GetRequiredService<IAnalytics>();
            return MobileAnalytics.Identity;
        }
    }
}
