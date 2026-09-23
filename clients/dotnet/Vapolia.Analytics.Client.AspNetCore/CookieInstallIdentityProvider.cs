using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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

            // An answer given during this request outranks the cookie the request came with.
            if (context.Items.TryGetValue(AnswerKey, out var answered) && answered is bool answer)
                return answer;

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
            if (context is null)
            {
                WarnNoHttpContext();
                return;
            }

            context.Items[AnswerKey] = value;

            if (value)
            {
                var installId = context.Items.TryGetValue(ItemKey, out var cached) && cached is string id
                    ? id
                    : Clean.InstallId(context.Request.Cookies[options.WebOptions.CookieName]);
                context.Items.Remove(ItemKey);
                if (installId is not null)
                    Sender?.Invoke()?.Purge(installId);
            }

            if (context.Response.HasStarted)
            {
                Logger?.LogWarning("analytics: the response has started, the opposition cookie cannot be written. Set IsOptedOut from a server-rendered request.");
                return;
            }

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
    /// Reads the cookie, creating it when the response has not started yet. Read by the middleware on
    /// the way in, so a component rendering later always finds one.
    /// </summary>
    public string? InstallId
    {
        get
        {
            var context = accessor.HttpContext;
            if (context is null)
            {
                WarnNoHttpContext();
                return null;
            }

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
    }

    /// <summary>
    /// The visitor's country, from <c>Accept-Language</c>: the browser's own setting, not a
    /// geolocation of the IP, which the collector forbids and never stores. The user agent is
    /// deliberately not parsed: guessing a device class from it is fingerprinting for a column
    /// nobody reads.
    /// </summary>
    public string? Country => RegionOf(PreferredLanguage(accessor.HttpContext));

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

    /// <summary>
    /// Interactive Blazor has no HttpContext: nothing is measured and no answer is stored there. Logged
    /// once per process.
    /// </summary>
    void WarnNoHttpContext()
    {
        if (Interlocked.Exchange(ref warnedNoHttpContext, 1) == 0)
            Logger?.LogWarning("analytics: no HttpContext, nothing is measured. Only server-rendered requests are supported, not interactive Blazor components.");
    }

    static int warnedNoHttpContext;

    internal ILogger? Logger { get; init; }

    /// <summary>The sender to purge on an opposition, null when nothing is measured.</summary>
    internal Func<Sender?>? Sender { get; init; }

    const string ItemKey = "vapolia.analytics.installId";
    const string AnswerKey = "vapolia.analytics.optedOut";
}
