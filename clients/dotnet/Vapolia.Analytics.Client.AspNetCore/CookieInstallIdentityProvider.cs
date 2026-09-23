using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Vapolia.Analytics.Client;

/// <summary>
/// The visitor's installation id, carried by a first-party cookie this server sets itself.
///
/// The cookie is written once and **never refreshed**: a sliding expiry would void the exemption.
/// When the browser drops it at expiry, the next visit creates a new one, which is the rotation.
///
/// It is HttpOnly: no script reads it, which is also why nothing shows up client-side.
/// </summary>
public sealed class CookieInstallIdentityProvider(IHttpContextAccessor accessor, IOptions<AnalyticsOptions> options) : IInstallContext
{
    readonly AnalyticsOptions options = options.Value;

    /// <summary>
    /// Whether this visitor has opposed the measurement. Before any answer it reads
    /// <see cref="AnalyticsWebOptions.RequiresPriorConsentForLocale"/>, then
    /// <see cref="AnalyticsOptions.RequiresPriorConsent"/>, then the regime of the
    /// <c>Accept-Language</c> tag: no identity cookie is written until the banner accepts.
    /// </summary>
    public bool IsOptedOut
    {
        get
        {
            var context = accessor.HttpContext;
            if (context is null)
                return Unanswered();

            return context.Request.Cookies[options.WebOptions.OptOutCookieName] switch
            {
                "1" => true,
                "0" => false,
                _ => Unanswered(),
            };

            bool Unanswered()
            {
                if (options.WebOptions.RequiresPriorConsentForLocale is { } byLocale)
                    return byLocale(PreferredLanguage(context));

                return options.RequiresPriorConsent
                       ?? PriorConsentCountries.LocaleRequiresPriorConsent(PreferredLanguage(context));
            }
        }
        set
        {
            var context = accessor.HttpContext;
            if (context is null || context.Response.HasStarted)
                return;

            if (value)
            {
                context.Response.Cookies.Append(options.WebOptions.OptOutCookieName, "1", new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    IsEssential = true,
                    Expires = DateTimeOffset.UtcNow.Add(options.AdvancedOptions.OptOutLifetime),
                });

                context.Response.Cookies.Delete(options.WebOptions.CookieName);
                context.Items.Remove(ItemKey);
                return;
            }

            // "0", not a deletion: an acceptance has to outrank RequiresPriorConsent on the next request.
            context.Response.Cookies.Append(options.WebOptions.OptOutCookieName, "0", new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Expires = DateTimeOffset.UtcNow.Add(options.AdvancedOptions.OptOutLifetime),
            });
        }
    }


    /// <summary>
    /// Reads the cookie, creating it when the response has not started yet. Called by the middleware
    /// on the way in, so a component rendering later always finds one.
    /// </summary>
    public string? GetInstallId()
    {
        var context = accessor.HttpContext;
        if (context is null)
            return null;

        // Checked first: an opposed visitor gets no identifier written at all, not one written then
        // ignored.
        if (IsOptedOut)
            return null;

        if (context.Items.TryGetValue(ItemKey, out var cached) && cached is string existing)
            return existing;

        var fromCookie = Clean.InstallId(context.Request.Cookies[options.WebOptions.CookieName]);
        if (fromCookie is not null)
        {
            context.Items[ItemKey] = fromCookie;
            return fromCookie;
        }

        // A cookie can only be set before the response starts. After that the visitor is measured on
        // the next request instead — one missed hit, never a wrong one.
        if (context.Response.HasStarted)
            return null;

        var issued = Guid.NewGuid().ToString("D");
        context.Response.Cookies.Append(options.WebOptions.CookieName, issued, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            // Absolute, from this moment. Never extended on a later visit.
            Expires = DateTimeOffset.UtcNow.Add(options.AdvancedOptions.InstallIdLifetime),
        });

        context.Items[ItemKey] = issued;
        return issued;
    }

    /// <summary>
    /// The device of a browser, without a single line of script. Nothing about the app itself is here:
    /// that is the batch context, which an IAnalyticsContext supplies per request.
    ///
    /// The country comes from <c>Accept-Language</c>, which is the visitor's own browser setting —
    /// not a geolocation of the IP, which the collector forbids and never stores. The user agent is
    /// deliberately not parsed: guessing a device class from it is fingerprinting for a column
    /// nobody reads.
    /// </summary>
    public Device GetDevice()
    {
        var context = accessor.HttpContext;

        return new Device { Country = RegionOf(PreferredLanguage(context)) };
    }

    static string? PreferredLanguage(HttpContext? context)
    {
        var header = context?.Request.Headers.AcceptLanguage.ToString();
        if (string.IsNullOrWhiteSpace(header))
            return null;

        // "fr-FR,fr;q=0.9,en;q=0.8" — the first tag is the preferred one.
        var first = header.Split(',')[0].Split(';')[0].Trim();
        return first.Length == 0 ? null : first;
    }

    static string? RegionOf(string? languageTag)
    {
        if (string.IsNullOrEmpty(languageTag))
            return null;

        try
        {
            var culture = CultureInfo.GetCultureInfo(languageTag);
            if (culture.IsNeutralCulture)
                return null;

            return new RegionInfo(culture.Name).TwoLetterISORegionName;
        }
        catch (Exception e) when (e is CultureNotFoundException or ArgumentException)
        {
            // An unreadable tag is an unknown country, never a guessed one.
            return null;
        }
    }

    const string ItemKey = "vapolia.analytics.installId";
}
