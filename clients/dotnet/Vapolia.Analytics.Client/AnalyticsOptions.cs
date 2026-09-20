namespace Vapolia.Analytics.Client;

/// <summary>
/// Client configuration.
/// </summary>
public sealed class AnalyticsOptions
{
    /// <summary>The collector's ingestion URL (https://baseUrl/sourceName)</summary>
    public required Uri IngestionUrl { get; set; }
    
    /// <summary>
    /// The server-to-server key.
    /// TODO: merge ApiKey with AccessToken, keep only one of the 2, to simplify client config.
    /// </summary>
    /// <remarks>
    /// for server to server analytics only.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The device-to-server key.
    /// TODO: merge ApiKey with AccessToken, keep only one of the 2, to simplify client config.
    /// </summary>
    /// <remarks>
    /// for device to server analytics only.
    /// </remarks>
    public string? AccessToken { get; set; }
    
    /// <summary>
    /// The identity of this installation
    /// </summary>
    public Func<InstallSeed?>? SeedInstallId { get; set; }

    /// <summary>
    /// Toggles collection of analytics
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// ISO 3166-1 alpha-2 countries excluded from the collection.
    /// Should be a copy of the exclusion list of the collector (the analytics server).
    /// </summary>
    public ICollection<string> ExcludedCountries { get; set; } = [];

    /// <summary>
    /// Context sent with each batch of events.
    /// Every key must be on the source's <c>context</c> whitelist.
    /// If <see cref="IAnalyticsContext"/> is registered, it wins over this delegate.
    /// </summary>
    public Func<IReadOnlyDictionary<string, object?>?>? Context { get; set; }

    /// <summary>
    /// Uncommon options
    /// </summary>
    public AnalyticsAdvancedOptions AdvancedOptions { get; set; } = new();
    
    /// <summary>
    /// Uncommon Apps-only options
    /// </summary>
    public AnalyticsAppOptions AppOptions { get; set; } = new();
    
    /// <summary>
    /// Uncommon Web-only options
    /// </summary>
    public AnalyticsWebOptions WebOptions { get; set; } = new();
}