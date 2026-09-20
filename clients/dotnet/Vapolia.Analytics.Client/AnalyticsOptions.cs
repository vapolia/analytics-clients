namespace Vapolia.Analytics.Client;

/// <summary>
/// Client configuration.
/// </summary>
public sealed class AnalyticsOptions
{
    /// <summary>The collector's ingestion URL (https://baseUrl/sourceName)</summary>
    public required Uri IngestionUrl { get; set; }
    
    /// <summary>
    /// The credential sent as <c>Authorization: Bearer</c> — a server token for server-to-server
    /// analytics, or a build token for device-to-server analytics. The collector tells the two apart
    /// from the token itself, not from how it arrived, so one property covers both roles.
    /// </summary>
    public string? Token { get; set; }
    
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