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
        get => ConsentAnswer is { } answer ? !answer : RequiresPriorConsent;
        set
        {
            var context = accessor.HttpContext;
            if (context is null)
            {
                WarnNoHttpContext();
                return;
            }

            context.Items[AnswerKey] = !value;

            if (value)
            {
                var installId = context.Items.TryGetValue(ItemKey, out var cached) && cached is string id
                    ? id
                    : Clean.InstallId(context.Request.Cookies[Web.IdCookie]) ?? LegacyInstallId(context);
                context.Items.Remove(ItemKey);
                if (installId is not null)
                    Sender?.Invoke()?.Purge(installId);
            }

            if (context.Response.HasStarted)
            {
                Logger?.LogWarning("Opposition cookie cannot be written: http response has already started. Set IsOptedOut from a server-rendered request");
                return;
            }

            // "0", not a deletion: an acceptance has to outrank RequiresPriorConsent on the next request.
            context.Response.Cookies.Append(Web.AnswerCookie, value ? "1" : "0", Cookie(options.AdvancedOptions.OptOutLifetime));
            if (Web.AnswerCookie != AnalyticsWebOptions.LegacyAnswerCookie)
                DeleteLegacy(context, AnalyticsWebOptions.LegacyAnswerCookie);

            if (value)
            {
                context.Response.Cookies.Delete(Web.IdCookie, Cookie(TimeSpan.Zero));
                if (Web.IdCookie != AnalyticsWebOptions.LegacyIdCookie)
                    DeleteLegacy(context, AnalyticsWebOptions.LegacyIdCookie);
            }
        }
    }

    AnalyticsWebOptions Web => options.WebOptions;

    /// <summary>The attributes of both cookies, expiring <paramref name="lifetime"/> from now.</summary>
    CookieOptions Cookie(TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/",
        Domain = string.IsNullOrEmpty(Web.CookieDomain) ? null : Web.CookieDomain,
        Expires = DateTimeOffset.UtcNow.Add(lifetime),
    };

    /// <summary>The id held under the name used before the prefixes, when the current name is another one.</summary>
    string? LegacyInstallId(HttpContext context)
        => Web.IdCookie == AnalyticsWebOptions.LegacyIdCookie ? null : Clean.InstallId(context.Request.Cookies[AnalyticsWebOptions.LegacyIdCookie]);

    // The legacy cookies were set on the host, with no domain.
    static void DeleteLegacy(HttpContext context, string name)
        => context.Response.Cookies.Delete(name, new CookieOptions { Path = "/", Secure = true });

    /// <summary>
    /// The visitor's answer: true accepted, false refused, null unanswered. Read from
    /// <see cref="AnalyticsWebOptions.OptOutCookieName"/>, or from <see cref="IsOptedOut"/> when it was
    /// set during this request.
    /// </summary>
    public bool? ConsentAnswer
    {
        get
        {
            var context = accessor.HttpContext;
            if (context is null)
                return null;

            if (context.Items.TryGetValue(AnswerKey, out var answered) && answered is bool answer)
                return answer;

            var current = Read(context.Request.Cookies[Web.AnswerCookie]);
            if (current is not null || Web.AnswerCookie == AnalyticsWebOptions.LegacyAnswerCookie)
                return current;

            // An answer given under the legacy name is moved to the current one.
            if (Read(context.Request.Cookies[AnalyticsWebOptions.LegacyAnswerCookie]) is not { } legacy)
                return null;

            context.Items[AnswerKey] = legacy;
            if (!context.Response.HasStarted)
            {
                context.Response.Cookies.Append(Web.AnswerCookie, legacy ? "0" : "1", Cookie(options.AdvancedOptions.OptOutLifetime));
                DeleteLegacy(context, AnalyticsWebOptions.LegacyAnswerCookie);
            }

            return legacy;

            static bool? Read(string? value) => value switch
            {
                "1" => false,
                "0" => true,
                _ => null,
            };
        }
    }

    /// <summary>
    /// Whether the regime of this request requires consent before anything is stored, whatever the
    /// visitor answered. Reads <see cref="AnalyticsWebOptions.RequiresPriorConsentForLocale"/>, then
    /// <see cref="AnalyticsOptions.RequiresPriorConsent"/>, then the regime of the
    /// <c>Accept-Language</c> tag.
    /// </summary>
    public bool RequiresPriorConsent
    {
        get
        {
            var language = PreferredLanguage(accessor.HttpContext);
            if (options.WebOptions.RequiresPriorConsentForLocale is { } byLocale)
                return byLocale(language);

            return options.RequiresPriorConsent ?? PriorConsentCountries.LocaleRequiresPriorConsent(language);
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

            var fromCookie = Clean.InstallId(context.Request.Cookies[Web.IdCookie]);
            if (fromCookie is not null)
            {
                context.Items[ItemKey] = fromCookie;
                return fromCookie;
            }

            // An id held under the legacy name is moved to the current one, with a full lifetime: the only extension an id ever gets.
            if (LegacyInstallId(context) is { } legacy)
            {
                context.Items[ItemKey] = legacy;
                if (!context.Response.HasStarted)
                {
                    context.Response.Cookies.Append(Web.IdCookie, legacy, Cookie(options.AdvancedOptions.InstallIdLifetime));
                    DeleteLegacy(context, AnalyticsWebOptions.LegacyIdCookie);
                }

                return legacy;
            }

            // A cookie can only be set before the response starts. After that the visitor is measured on
            // the next request instead — one missed hit, never a wrong one.
            if (context.Response.HasStarted)
                return null;

            var issued = Guid.NewGuid().ToString("D");
            // Absolute, from this moment. Never extended on a later visit.
            context.Response.Cookies.Append(Web.IdCookie, issued, Cookie(options.AdvancedOptions.InstallIdLifetime));

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
    public string? Country => Bcp47.Split(PreferredLanguage(accessor.HttpContext)).Region;

    /// <summary>
    /// The visitor's address, from <c>Connection.RemoteIpAddress</c>. Behind a proxy, the
    /// <c>ForwardedHeaders</c> middleware sets it from the proxy's header.
    /// </summary>
    public string? ClientAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    static string? PreferredLanguage(HttpContext? context)
    {
        var header = context?.Request.Headers.AcceptLanguage.ToString();
        if (string.IsNullOrWhiteSpace(header))
            return null;

        // "fr-FR,fr;q=0.9,en;q=0.8" — the first tag is the preferred one.
        var first = header.Split(',')[0].Split(';')[0].Trim();
        return first.Length == 0 ? null : first;
    }

    /// <summary>
    /// Interactive Blazor has no HttpContext: nothing is measured and no answer is stored there. Logged
    /// once per process.
    /// </summary>
    void WarnNoHttpContext()
    {
        if (Interlocked.Exchange(ref warnedNoHttpContext, 1) == 0)
            Logger?.LogWarning("No HttpContext: only server-rendered requests are supported, not interactive Blazor components");
    }

    static int warnedNoHttpContext;

    internal ILogger? Logger { get; init; }

    /// <summary>The sender to purge on an opposition, null when nothing is measured.</summary>
    internal Func<Sender?>? Sender { get; init; }

    const string ItemKey = "vapolia.analytics.installId";
    const string AnswerKey = "vapolia.analytics.consentAnswer";
}
