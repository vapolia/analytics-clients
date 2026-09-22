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
        var options = new AnalyticsOptions { IngestionUrl = new Uri("https://localhost/testsource") };
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

        var id = provider.GetInstallId();

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

        var id = provider.GetInstallId();

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

        var id = provider.GetInstallId();

        Assert.IsNotNull(Clean.InstallId(id));
        Assert.AreNotEqual("not-a-guid", id);
    }

    [TestMethod]
    public void ResolvesTheSameIdTwiceWithinOneRequest()
    {
        var (provider, _) = Create();

        Assert.AreEqual(provider.GetInstallId(), provider.GetInstallId());
    }

    [TestMethod]
    public void AnOpposedVisitorGetsNoIdAndNoCookie()
    {
        var (provider, context) = Create(optedOut: true);

        Assert.IsTrue(provider.OptedOut);
        Assert.IsNull(provider.GetInstallId());
        Assert.AreEqual(
            "",
            context.Response.Headers.SetCookie.ToString(),
            "an opposed visitor gets no identifier written at all");
    }

    [TestMethod]
    public void OpposingDropsTheIdentityCookie()
    {
        var (provider, context) = Create(cookie: InstallId);
        Assert.AreEqual(InstallId, provider.GetInstallId());

        provider.OptedOut = true;

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.IsTrue(setCookie.Contains("_vau_off=1", StringComparison.Ordinal), setCookie);
        // Deleting a cookie is expiring it in the past.
        Assert.IsTrue(setCookie.Contains("_vau=;", StringComparison.Ordinal), setCookie);
    }

    [TestMethod]
    public void LiftingTheRefusalRecordsTheAcceptance()
    {
        var (provider, context) = Create(optedOut: true);

        provider.OptedOut = false;

        var setCookie = context.Response.Headers.SetCookie.ToString();
        // "0", not a deletion: the acceptance has to outrank DefaultOptedOut on the next request.
        Assert.IsTrue(setCookie.Contains("_vau_off=0", StringComparison.Ordinal), setCookie);
    }

    /// A country that asks first: no identity cookie until the banner accepts.
    [TestMethod]
    public void AnUnansweredConsentRegimeGetsNoIdAndNoCookie()
    {
        var (provider, context) = Create(configure: o => o.DefaultOptedOut = true);

        Assert.IsTrue(provider.OptedOut);
        Assert.IsNull(provider.GetInstallId());
        Assert.AreEqual("", context.Response.Headers.SetCookie.ToString());
    }

    [TestMethod]
    public void AnAcceptanceOutranksTheDefault()
    {
        var (provider, _) = Create(accepted: true, configure: o => o.DefaultOptedOut = true);

        Assert.IsFalse(provider.OptedOut);
        Assert.IsNotNull(Clean.InstallId(provider.GetInstallId()));
    }

    [TestMethod]
    public void TheRegimeIsReadFromTheVisitorsCountry()
    {
        var consent = new HashSet<string> { "DE" };
        void Regime(AnalyticsOptions o)
            => o.WebOptions.DefaultOptedOutForCountry = country => country is not null && consent.Contains(country);

        // One visitor at a time: HttpContextAccessor keeps the current context in an AsyncLocal, so
        // two providers built side by side would both read the last one.
        var (german, _) = Create(acceptLanguage: "de-DE", configure: Regime);
        Assert.IsTrue(german.OptedOut);

        var (french, _) = Create(acceptLanguage: "fr-FR", configure: Regime);
        Assert.IsFalse(french.OptedOut);
    }

    [TestMethod]
    public void ReadsTheCountryFromAcceptLanguage()
    {
        var (provider, _) = Create(acceptLanguage: "fr-FR,fr;q=0.9,en;q=0.8");

        var device = provider.GetDevice();

        Assert.AreEqual("FR", device.Country);
    }

    [TestMethod]
    public void ANeutralLanguageHasNoCountry()
    {
        var (provider, _) = Create(acceptLanguage: "fr");

        var device = provider.GetDevice();

        Assert.IsNull(device.Country, "a language without a region is an unknown country, never a guessed one");
    }

    [TestMethod]
    public void AnUnreadableLanguageIsIgnored()
    {
        var (provider, _) = Create(acceptLanguage: "%%%");

        var device = provider.GetDevice();

        Assert.IsNull(device.Country);
    }

    [TestMethod]
    public void NoHeaderMeansNoCountry()
    {
        var (provider, _) = Create();

        var device = provider.GetDevice();

        Assert.IsNull(device.Country);
    }
}
