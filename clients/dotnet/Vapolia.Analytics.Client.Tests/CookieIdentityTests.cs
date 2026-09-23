using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Vapolia.Analytics.Client.Tests;

[TestClass]
public class CookieIdentityTests
{
    const string InstallId = "11111111-0000-0000-0000-000011111111";

    static (CookieInstallIdentityProvider Provider, HttpContext Context) Create(
        string? cookie = null,
        string? acceptLanguage = null,
        bool optedOut = false,
        bool accepted = false,
        Action<AnalyticsOptions>? configure = null)
    {
        // False, not null: a null RequiresPriorConsent reads the visitor's locale, which is its own
        // test below.
        var options = new AnalyticsOptions { IngestionUrl = new Uri("https://localhost/testsource"), RequiresPriorConsent = false };
        configure?.Invoke(options);
        var context = new DefaultHttpContext();

        var cookies = new List<string>();
        if (cookie is not null)
            cookies.Add($"{options.WebOptions.CookieName}={cookie}");
        if (optedOut)
            cookies.Add($"{options.WebOptions.OptOutCookieName}=1");
        if (accepted)
            cookies.Add($"{options.WebOptions.OptOutCookieName}=0");
        if (cookies.Count > 0)
            context.Request.Headers.Cookie = string.Join("; ", cookies);

        if (acceptLanguage is not null)
            context.Request.Headers.AcceptLanguage = acceptLanguage;

        var accessor = new HttpContextAccessor { HttpContext = context };
        return (new CookieInstallIdentityProvider(accessor, Options.Create(options)), context);
    }

    [TestMethod]
    public void IssuesACookieOnTheFirstVisit()
    {
        var (provider, context) = Create();

        var id = provider.InstallId;

        Assert.IsNotNull(Clean.InstallId(id));
        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.IsTrue(setCookie.Contains(id!, StringComparison.Ordinal), setCookie);
        Assert.IsTrue(setCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase), setCookie);
        Assert.IsTrue(setCookie.Contains("secure", StringComparison.OrdinalIgnoreCase), setCookie);
        Assert.IsTrue(setCookie.Contains("expires", StringComparison.OrdinalIgnoreCase), setCookie);
    }

    [TestMethod]
    public void ReusesTheCookieAndNeverExtendsIt()
    {
        var (provider, context) = Create(cookie: InstallId);

        var id = provider.InstallId;

        Assert.AreEqual(InstallId, id);
        Assert.AreEqual(
            "",
            context.Response.Headers.SetCookie.ToString(),
            "re-issuing the cookie would slide its expiry, which the 13-month ceiling forbids");
    }

    [TestMethod]
    public void ReplacesAnUnreadableCookie()
    {
        var (provider, _) = Create(cookie: "not-a-guid");

        var id = provider.InstallId;

        Assert.IsNotNull(Clean.InstallId(id));
        Assert.AreNotEqual("not-a-guid", id);
    }

    [TestMethod]
    public void ResolvesTheSameIdTwiceWithinOneRequest()
    {
        var (provider, _) = Create();

        Assert.AreEqual(provider.InstallId, provider.InstallId);
    }

    [TestMethod]
    public void AnOpposedVisitorGetsNoIdAndNoCookie()
    {
        var (provider, context) = Create(optedOut: true);

        Assert.IsTrue(provider.IsOptedOut);
        Assert.IsNull(provider.InstallId);
        Assert.AreEqual(
            "",
            context.Response.Headers.SetCookie.ToString(),
            "an opposed visitor gets no identifier written at all");
    }

    [TestMethod]
    public void OpposingDropsTheIdentityCookie()
    {
        var (provider, context) = Create(cookie: InstallId);
        Assert.AreEqual(InstallId, provider.InstallId);

        provider.IsOptedOut = true;

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.IsTrue(setCookie.Contains("_vau_off=1", StringComparison.Ordinal), setCookie);
        // Deleting a cookie is expiring it in the past.
        Assert.IsTrue(setCookie.Contains("_vau=;", StringComparison.Ordinal), setCookie);
    }

    [TestMethod]
    public void LiftingTheRefusalRecordsTheAcceptance()
    {
        var (provider, context) = Create(optedOut: true);

        provider.IsOptedOut = false;

        var setCookie = context.Response.Headers.SetCookie.ToString();
        // "0", not a deletion: the acceptance has to outrank RequiresPriorConsent on the next request.
        Assert.IsTrue(setCookie.Contains("_vau_off=0", StringComparison.Ordinal), setCookie);
    }

    /// A country that asks first: no identity cookie until the banner accepts.
    [TestMethod]
    public void AnUnansweredConsentRegimeGetsNoIdAndNoCookie()
    {
        var (provider, context) = Create(configure: o => o.RequiresPriorConsent = true);

        Assert.IsTrue(provider.IsOptedOut);
        Assert.IsNull(provider.InstallId);
        Assert.AreEqual("", context.Response.Headers.SetCookie.ToString());
    }

    [TestMethod]
    public void AnAcceptanceOutranksTheDefault()
    {
        var (provider, _) = Create(accepted: true, configure: o => o.RequiresPriorConsent = true);

        Assert.IsFalse(provider.IsOptedOut);
        Assert.IsNotNull(Clean.InstallId(provider.InstallId));
    }

    [TestMethod]
    public void TheRegimeIsReadFromTheVisitorsLocale()
    {
        var consent = new HashSet<string> { "de-DE" };
        void Regime(AnalyticsOptions o)
            => o.WebOptions.RequiresPriorConsentForLocale = locale => locale is not null && consent.Contains(locale);

        // One visitor at a time: HttpContextAccessor keeps the current context in an AsyncLocal, so
        // two providers built side by side would both read the last one.
        var (german, _) = Create(acceptLanguage: "de-DE", configure: Regime);
        Assert.IsTrue(german.IsOptedOut);

        var (french, _) = Create(acceptLanguage: "fr-FR", configure: Regime);
        Assert.IsFalse(french.IsOptedOut);
    }

    /// Null falls back to the shipped list, on the visitor's own locale.
    [TestMethod]
    public void AnUnsetRegimeIsReadFromTheVisitorsLocale()
    {
        void Unset(AnalyticsOptions o) => o.RequiresPriorConsent = null;

        var (german, _) = Create(acceptLanguage: "de-DE", configure: Unset);
        Assert.IsTrue(german.IsOptedOut);

        var (quebecer, _) = Create(acceptLanguage: "fr-CA", configure: Unset);
        Assert.IsTrue(quebecer.IsOptedOut);

        var (canadian, _) = Create(acceptLanguage: "en-CA", configure: Unset);
        Assert.IsFalse(canadian.IsOptedOut);

        var (french, _) = Create(acceptLanguage: "fr-FR", configure: Unset);
        Assert.IsFalse(french.IsOptedOut);

        var (unknown, _) = Create(configure: Unset);
        Assert.IsTrue(unknown.IsOptedOut, "no Accept-Language is an unreadable region");
    }

    [TestMethod]
    public void ReadsTheCountryFromAcceptLanguage()
    {
        var (provider, _) = Create(acceptLanguage: "fr-FR,fr;q=0.9,en;q=0.8");

        var country = provider.Country;

        Assert.AreEqual("FR", country);
    }

    [TestMethod]
    public void ANeutralLanguageHasNoCountry()
    {
        var (provider, _) = Create(acceptLanguage: "fr");

        var country = provider.Country;

        Assert.IsNull(country, "a language without a region is an unknown country, never a guessed one");
    }

    [TestMethod]
    public void AnUnreadableLanguageIsIgnored()
    {
        var (provider, _) = Create(acceptLanguage: "%%%");

        var country = provider.Country;

        Assert.IsNull(country);
    }

    [TestMethod]
    public void NoHeaderMeansNoCountry()
    {
        var (provider, _) = Create();

        var country = provider.Country;

        Assert.IsNull(country);
    }

    [TestMethod]
    public void AnOppositionTakesEffectWithinTheSameRequest()
    {
        var (provider, _) = Create(cookie: InstallId);
        Assert.AreEqual(InstallId, provider.InstallId);

        provider.IsOptedOut = true;

        Assert.IsTrue(provider.IsOptedOut);
        Assert.IsNull(provider.InstallId);
    }

    [TestMethod]
    public void AnAcceptanceTakesEffectWithinTheSameRequest()
    {
        var (provider, _) = Create(configure: o => o.RequiresPriorConsent = true);
        Assert.IsNull(provider.InstallId);

        provider.IsOptedOut = false;

        Assert.IsNotNull(provider.InstallId);
    }

    [TestMethod]
    public async Task AnOppositionPurgesTheVisitorsQueuedEvents()
    {
        var collector = new FakeCollector();
        var options = new AnalyticsOptions { IngestionUrl = new Uri("https://localhost/testsource"), RequiresPriorConsent = false };
        options.AdvancedOptions.FlushInterval = TimeSpan.FromHours(1);
        await using var sender = new Sender(options, collector, null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{options.WebOptions.CookieName}={InstallId}";
        var provider = new CookieInstallIdentityProvider(new HttpContextAccessor { HttpContext = context }, Options.Create(options))
        {
            Sender = () => sender,
        };
        var analytics = new Analytics(sender, provider, options: options);

        analytics.Track("app_open");
        provider.IsOptedOut = true;
        analytics.Track("game_end");
        await sender.FlushAsync();

        Assert.AreEqual(0, collector.Bodies.Count);
        Assert.AreEqual(1, sender.Stats.Dropped);
        Assert.AreEqual(1, sender.Stats.Rejected);
    }
}
