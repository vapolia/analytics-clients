using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vapolia.Analytics.Client;

/// <summary>
/// The mobile entry point for an app with no dependency-injection container — one call at startup,
/// one call per event:
///
/// <code>
/// MobileAnalytics.Start(new AnalyticsOptions { Source = "&lt;sourceName&gt;" });
/// MobileAnalytics.Current.Track("game_end", ("result", "win"), ("moves", 34));
/// </code>
///
/// With a container, call <c>services.AddAnalytics(o => o.IngestionUrl = new("https://analytics.example.com/&lt;sourceName&gt;"))</c> instead and inject
/// <see cref="IAnalytics"/>: it starts this same client and registers the identity with it.
///
/// Either way the client owns the installation id, the device context and the flush when the app
/// goes to the background. It emits no event of its own: naming what is measured is the app's, and
/// the whitelist's, business.
/// </summary>
public static class MobileAnalytics
{
    static Sender? sender;
    static IAnalytics? current;
    static readonly Lock Gate = new();
    static readonly AppLifecycleEvents lifecycle = new();

    /// <summary>
    /// The client. Before <see cref="Start"/> it is a working no-op, so a call site never has to check.
    /// </summary>
    public static IAnalytics Current => current ?? NullAnalytics.Instance;

    /// <summary>
    /// The installation identity, once started: the opposition switch lives here, and so does the
    /// device context an app can complete with <see cref="MobileInstallIdentityProvider.Update"/>.
    /// </summary>
    public static MobileInstallIdentityProvider? Identity { get; private set; }

    /// <summary>
    /// When the app leaves the foreground and when it comes back — what the client already watches to
    /// flush, exposed so an app naming its own <c>app_open</c> does not subscribe to the platform a
    /// second time. Subscriptions survive <see cref="StopAsync"/>.
    /// </summary>
    public static IAppLifecycle Lifecycle => lifecycle;

    /// <summary>Starts the sender. Cannot be called twice.</summary>
    /// <param name="options">At least <see cref="AnalyticsOptions.IngestionUrl"/>.</param>
    /// <param name="logger">Where transport failures go. Nothing is logged when null.</param>
    /// <param name="context">Asked for the batch context on every event.</param>
    /// <param name="httpClient">An HttpClient to reuse. One is created when null.</param>
    /// <param name="timeZone">Whose clock the events are dated against. The device's own when null.</param>
    /// <returns>The client, the same instance as <see cref="Current"/>.</returns>
    public static IAnalytics Start(
        AnalyticsOptions options,
        ILogger? logger = null,
        IAnalyticsContext? context = null,
        HttpClient? httpClient = null,
        IAnalyticsTimeZone? timeZone = null)
    {
        lock (Gate)
        {
            if (options.IsDebugBuild)
                return NullAnalytics.Instance;

            if (current is not null)
                return current;

            if (!options.IngestionUrl.IsAbsoluteUri)
                throw new InvalidOperationException($"Invalid {nameof(AnalyticsOptions)}.{nameof(AnalyticsOptions.IngestionUrl)}: must be an absolute URL '{options.IngestionUrl}'");

            httpClient ??= options.AdvancedOptions.CreateHttpClient?.Invoke() ?? new HttpClient { Timeout = options.AdvancedOptions.RequestTimeout };
            options.AdvancedOptions.SpoolPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vapolia-analytics-spool.json");

            var identity = new MobileInstallIdentityProvider(options);
            var spool = new PersistPendingItemsToLocalStorageHelper(options.AdvancedOptions.SpoolPath, options.AdvancedOptions.SpoolCapacity, logger);

            // What a previous session spooled must not leave while the person is opted out — or has
            // not yet answered, under a regime that asks first.
            if (identity.IsOptedOut)
                spool.Save([]);

            var started = new Sender(
                options,
                new HttpPublishHelper(httpClient, options.IngestionUrl, options.Token),
                spool,
                logger ?? NullLogger.Instance);

            sender = started;
            Identity = identity;
            current = new Analytics(started, identity, context, options, timeZone ?? new DeviceTimeZone());

            Bootstrap(started, options);
            return current;
        }
    }

    /// <summary>
    /// Sends what is queued and writes down the rest. Called on its own when the app goes to the
    /// background, unless <see cref="AnalyticsAppOptions.FlushesOnBackground"/> was turned off.
    /// </summary>
    /// <param name="cancellationToken">Gives up waiting; the queue is spooled either way.</param>
    public static Task FlushAsync(CancellationToken cancellationToken = default)
        => sender?.FlushAsync(persist: true, cancellationToken) ?? Task.CompletedTask;

    /// <summary>One last flush, then the sender stops. Tracking afterwards is a no-op.</summary>
    public static async Task StopAsync()
    {
        Sender? stopping;
        lock (Gate)
        {
            stopping = sender;
            sender = null;
            current = null;
            Identity = null;
        }

        if (stopping is not null)
            await stopping.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Auto flush when the app is backgrounded
    /// </summary>
    static void Bootstrap(Sender started, AnalyticsOptions options)
        => AppLifecycle.Subscribe(
            onForeground: () => lifecycle.RaiseForeground(),
            onBackground: () =>
            {
                lifecycle.RaiseBackground();
                if (options.AppOptions.FlushesOnBackground)
                    _ = started.FlushAsync(persist: true);
            });
}
