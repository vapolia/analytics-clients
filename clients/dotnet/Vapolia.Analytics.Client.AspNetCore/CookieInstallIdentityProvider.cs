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
public sealed class CookieInstallIdentityProvider(
    IHttpContextAccessor accessor,
    IOptions<AnalyticsOptions> options) : IInstallIdentityProvider, IAnalyticsOptOut
{
    readonly AnalyticsOptions options = options.Value;

    /// <summary>Whether this visitor has opposed the measurement.</summary>
    public bool OptedOut
    {
        get
        {
            var context = accessor.HttpContext;
            return context is not null && context.Request.Cookies[options.OptOutCookieName] == "1";
        }
    }

    /// <summary>
    /// The right of opposition, for a settings page: call it from a handler that has not written its
    /// response yet.
    ///
    /// Opting out drops the identity cookie, so opting back in cannot resume the same installation.
    /// The opposition cookie itself **is** refreshed on each visit, unlike the identifier: extending
    /// a refusal serves the person, extending an identifier does not.
    /// </summary>
    public void SetOptedOut(bool value)
    {
        var context = accessor.HttpContext;
        if (context is null || context.Response.HasStarted)
            return;

        if (value)
        {
            context.Response.Cookies.Append(options.OptOutCookieName, "1", new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Expires = DateTimeOffset.UtcNow.Add(options.OptOutLifetime),
            });

            context.Response.Cookies.Delete(options.CookieName);
            context.Items.Remove(ItemKey);
            return;
        }

        context.Response.Cookies.Delete(options.OptOutCookieName);
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
        if (OptedOut)
            return null;

        if (context.Items.TryGetValue(ItemKey, out var cached) && cached is string existing)
            return existing;

        var fromCookie = Clean.InstallId(context.Request.Cookies[options.CookieName]);
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
        context.Response.Cookies.Append(options.CookieName, issued, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            // Absolute, from this moment. Never extended on a later visit.
            Expires = DateTimeOffset.UtcNow.Add(options.InstallIdLifetime),
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
