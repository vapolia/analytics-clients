namespace Vapolia.Analytics.Client;

/// <summary>
/// The default answer for <see cref="AnalyticsOptions.RequiresPriorConsent"/>, from the regimes
/// listed in OBLIGATIONS.md. Left null, the client asks this itself, with the device locale.
/// </summary>
public static class PriorConsentCountries
{
    /// <summary>
    /// The EEA outside France, Italy, Spain and the Netherlands, plus the United Kingdom.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "CY", "CZ", "DE", "DK", "EE", "FI", "GB", "GR", "HR", "HU",
        "IE", "IS", "LI", "LT", "LU", "LV", "MT", "NO", "PL", "PT", "RO", "SE", "SI", "SK",
    };

    /// <summary>
    /// Whether <paramref name="locale"/> — a BCP-47 tag such as <c>fr-FR</c>, the device locale — asks
    /// before anything may be stored. True when the tag carries no region.
    ///
    /// <c>fr-CA</c> answers true and <c>en-CA</c> false: Quebec asks first while the rest of Canada
    /// does not, and the language is the only signal a locale carries about it. A francophone outside
    /// Quebec is asked needlessly, an anglophone inside it is not asked at all.
    ///
    /// A starting point for your own counsel, not a legal opinion. Authority positions move, and this
    /// list moves only when the package is updated.
    /// </summary>
    public static bool LocaleRequiresPriorConsent(string? locale)
    {
        var (language, region) = Bcp47.Split(locale);
        if (region is null)
            return true;
        if (All.Contains(region))
            return true;
        return region.Equals("CA", StringComparison.OrdinalIgnoreCase)
               && language is not null
               && language.Equals("fr", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Enough BCP-47 to read a language and a region off a tag.</summary>
static class Bcp47
{
    /// <summary>
    /// The primary language subtag and the region subtag of <paramref name="locale"/>, both null when
    /// absent. The region is the first 2-letter subtag after the language, which skips a script
    /// (<c>zh-Hant-TW</c>) and a UN M.49 code (<c>es-419</c>, no region).
    /// </summary>
    public static (string? Language, string? Region) Split(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return (null, null);

        var parts = locale.Trim().Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        var language = parts[0].Length is >= 2 and <= 3 ? parts[0] : null;

        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 2 && parts[i].All(char.IsAsciiLetter))
                return (language, parts[i].ToUpperInvariant());
        }

        return (language, null);
    }
}
