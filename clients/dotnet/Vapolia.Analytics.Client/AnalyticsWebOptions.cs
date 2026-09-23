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
    /// Whether this visitor requires consent before anything is stored, given the BCP-47 tag read
    /// from <c>Accept-Language</c> — null when the header says none. Consulted only while the visitor
    /// has not answered, and it wins over <see cref="AnalyticsOptions.RequiresPriorConsent"/>.
    ///
    /// Leave it unset to take <see cref="PriorConsentCountries.LocaleRequiresPriorConsent"/>, which is
    /// what a null <see cref="AnalyticsOptions.RequiresPriorConsent"/> falls back to.
    /// </summary>
    public Func<string?, bool>? RequiresPriorConsentForLocale { get; set; }
}