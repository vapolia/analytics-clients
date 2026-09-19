namespace Vapolia.Analytics.Client;

/// <summary>
/// Everything the client needs beyond the app's identity. <see cref="Source"/> and
/// <see cref="Endpoint"/> have no default and must both be set.
/// </summary>
public sealed class AnalyticsOptions
{
    /// <summary>The app's registered name</summary>
    public string Source { get; set; } = "";

    /// <summary>The collector's base URL, e.g. "https://analytics.example.com". The source is appended to it.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>How long events are buffered before they go out.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Events accepted per <see cref="RateWindow"/>. Beyond it, everything is dropped until the
    /// window ends. It keeps a buggy client from getting its whole address throttled by the collector.
    /// Zero disables it, which is the default on the server side, where one process legitimately
    /// speaks for every visitor.
    /// </summary>
    public int MaxEventsPerWindow { get; set; } = 30;

    /// <summary>The window <see cref="MaxEventsPerWindow"/> counts in. Fixed, not sliding.</summary>
    public TimeSpan RateWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Events of one (install, device) group per request. Capped at the collector's ceiling.</summary>
    public int BatchSize { get; set; } = Clean.MaxEventsPerBatch;

    /// <summary>Events waiting for the sender. Beyond it Track drops rather than blocks.</summary>
    public int QueueCapacity { get; set; } = 4_000;

    /// <summary>Attempts per request before a batch is given up on.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>How long one request may take before it counts as a transient failure.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The server-to-server key.
    /// </summary>
    /// <remarks>
    /// for server to server analytics only
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The device-to-server key.
    /// </summary>
    /// <remarks>
    /// for device to server analytics only
    /// If missing the server returns HTTP 401
    /// </remarks>
    public string? AccessToken { get; set; }

    /// <summary>
    /// ISO 3166-1 alpha-2 countries not measured at all: nothing is sent from a device whose region is
    /// one of them. Copy the list that applies to this source — its own <c>excludedCountries</c> when
    /// it states one, otherwise the deployment's global list.
    /// </summary>
    public ICollection<string> ExcludedCountries { get; set; } = [];

    /// <summary>
    /// Written to disk so a restart does not lose what was queued. Null disables the spool — the
    /// sensible default for a web server, where a restart is rare and a lost count is cheap.
    /// </summary>
    public string? SpoolPath { get; set; }

    /// <summary>Events kept in the spool file.</summary>
    public int SpoolCapacity { get; set; } = 1_000;

    /// <summary>
    /// The cookie carrying the installation id on the web. Its lifetime is fixed when it is created
    /// and never extended on a later visit: a sliding expiry would void the exemption.
    /// </summary>
    public string CookieName { get; set; } = "_vau";

    /// <summary>
    /// 13 months minus a margin for clock drift. Both the cookie and the mobile identity use it.
    /// </summary>
    public TimeSpan InstallIdLifetime { get; set; } = TimeSpan.FromDays(390);

    /// <summary>The cookie recording a visitor's opposition. Present and set to "1" means opposed.</summary>
    public string OptOutCookieName { get; set; } = "_vau_off";

    /// <summary>
    /// How long a refusal is remembered. Unlike the identifier, it is refreshed on every visit:
    /// extending a refusal serves the person, extending an identifier does not.
    /// </summary>
    public TimeSpan OptOutLifetime { get; set; } = TimeSpan.FromDays(390);
    /// <summary>
    /// Turns the whole client into a no-op: nothing is queued, nothing is sent, no identifier is
    /// read or written. What a DEBUG build wants, without an <c>#if</c> at the registration site.
    /// It is also the only way to start without measuring: left enabled, a missing
    /// <see cref="Source"/> or <see cref="Endpoint"/> throws rather than going quiet.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the client flushes and spools when the app goes to the background — the last moment
    /// iOS guarantees the process runs. Mobile only; a server flushes when the host stops.
    /// </summary>
    public bool AutoFlushOnBackground { get; set; } = true;

    /// <summary>
    /// What is true of the installation for the whole batch, asked for again on every event, so an
    /// event carries the state it was produced under. Every key must be on the source's
    /// <c>context</c> whitelist. An app with a container registers an <see cref="IAnalyticsContext"/>
    /// instead, which wins over this one.
    /// </summary>
    public Func<IReadOnlyDictionary<string, object?>?>? Context { get; set; }

    /// <summary>
    /// The identity of an installation that was measured before this client, read only when the store
    /// is empty. Without it, switching an existing app over reissues every id at once. Idempotent by
    /// construction: once the id is stored, this is never called again.
    /// </summary>
    public Func<InstallSeed?>? SeedInstallId { get; set; }

    /// <summary>
    /// The HttpClient the sender uses. An app that wants its own handler, proxy or timeout — or one
    /// that already has an IHttpClientFactory — supplies it here:
    /// <c>o.CreateHttpClient = () => factory.CreateClient("Vapolia.Analytics")</c>. Null lets the
    /// client make its own, which is what a mobile app usually wants.
    ///
    /// A delegate rather than a factory resolved from the container: reading IHttpClientFactory would
    /// put Microsoft.Extensions.Http in the package, for an option most apps never set.
    /// </summary>
    public Func<HttpClient>? CreateHttpClient { get; set; }

    /// <summary>
    /// Opened around each request, and disposed after it. A flush triggered by the app going to the
    /// background is killed mid-send unless the platform has been told to hold the process — on iOS
    /// that is <c>beginBackgroundTask</c>. The app supplies its own scope; the client stays free of
    /// the native APIs.
    /// </summary>
    public Func<string, Task<IAsyncDisposable>>? BackgroundScope { get; set; }

    /// <summary>
    /// Called on every loss, next to the log: the events given up on (<c>permanent: false</c>, a
    /// network error or an unreachable collector), the ones the collector refused for good
    /// (<c>permanent: true</c>), and a saturated rate window. It exists so reporting to Sentry does
    /// not require routing this client's logger there.
    /// </summary>
    public Action<Exception?, string, bool>? OnError { get; set; }
}

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
