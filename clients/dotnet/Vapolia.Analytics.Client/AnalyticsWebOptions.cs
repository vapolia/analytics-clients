namespace Vapolia.Analytics.Client;

/// <summary>
/// Uncommon Web-only options
/// </summary>
public sealed class AnalyticsWebOptions
{
    /// <summary>
    /// The cookie carrying the installation id on the web. Its lifetime is fixed when it is created
    /// and never extended on a later visit: a sliding expiry would void the exemption.
    /// Null names it <c>__Host-vau</c>, or <c>__Secure-vau</c> when <see cref="CookieDomain"/> is set.
    /// </summary>
    public string? CookieName { get; set; }

    /// <summary>
    /// The cookie recording the visitor's answer: "1" opposed, "0" accepted, absent unanswered.
    /// Null names it <c>__Host-vau_off</c>, or <c>__Secure-vau_off</c> when <see cref="CookieDomain"/> is set.
    /// </summary>
    public string? OptOutCookieName { get; set; }

    /// <summary>
    /// The domain both cookies are shared on, <c>.example.com</c> for a site and its subdomains. Null keeps them on the host that
    /// set them, where no other subdomain can write them.
    /// </summary>
    public string? CookieDomain { get; set; }

    internal string IdCookie => CookieName ?? Prefix + "vau";
    internal string AnswerCookie => OptOutCookieName ?? Prefix + "vau_off";
    string Prefix => string.IsNullOrEmpty(CookieDomain) ? "__Host-" : "__Secure-";

    // The names before the prefixes. A visitor still holding them is moved to the current names on the next request.
    // This move can be removed from 2027-01-01.
    internal const string LegacyIdCookie = "_vau";
    internal const string LegacyAnswerCookie = "_vau_off";

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