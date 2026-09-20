namespace Vapolia.Analytics.Client;

/// <summary>
/// Uncommon Web-only options
/// </summary>
public sealed class AnalyticsWebOptions
{
    /// <summary>
    /// The cookie carrying the installation id on the web. Its lifetime is fixed when it is created
    /// and never extended on a later visit: a sliding expiry would void the exemption.
    /// </summary>
    public string CookieName { get; set; } = "_vau";

    /// <summary>The cookie recording a visitor's opposition. Present and set to "1" means opposed.</summary>
    public string OptOutCookieName { get; set; } = "_vau_off";
}