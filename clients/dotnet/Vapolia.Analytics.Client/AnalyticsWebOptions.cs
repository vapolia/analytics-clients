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

    /// <summary>
    /// The cookie recording the visitor's answer: "1" opposed, "0" accepted, absent unanswered.
    /// </summary>
    public string OptOutCookieName { get; set; } = "_vau_off";

    /// <summary>
    /// Whether this visitor's country requires consent before anything is stored, given the country
    /// read from the browser locale — null when it says none. Consulted only while the visitor has
    /// not answered, and it wins over <see cref="AnalyticsOptions.DefaultOptedOut"/>, which is the
    /// answer for a site serving one regime.
    /// </summary>
    public Func<string?, bool>? DefaultOptedOutForCountry { get; set; }
}