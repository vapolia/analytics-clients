using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Vapolia.Analytics.Client.Tests;

[TestClass]
public class RelayTests
{
    sealed class Recorder : IAnalytics
    {
        public List<(string Name, IReadOnlyDictionary<string, object?>? Props)> Tracked { get; } = [];

        public void Track(string name, IReadOnlyDictionary<string, object?>? props = null) => Tracked.Add((name, props));
        public void Track(string name, params ReadOnlySpan<(string Key, object? Value)> props) => Track(name, props.ToArray().ToDictionary(p => p.Key, p => p.Value));
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public AnalyticsStats Stats => default;
    }

    sealed class Install : IInstallContext
    {
        public string? InstallId => null;
        public string? Country => null;
        public bool IsOptedOut { get; set; }
        public bool? ConsentAnswer { get; set; }
        public bool RequiresPriorConsent { get; set; }
    }

    static (HttpContext Context, Recorder Analytics, Install Install) Request(string? body = null)
    {
        var recorder = new Recorder();
        var install = new Install();
        var services = new ServiceCollection()
            .AddSingleton<IAnalytics>(recorder)
            .AddSingleton<IInstallContext>(install)
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? ""));
        context.Response.Body = new MemoryStream();
        return (context, recorder, install);
    }

    static AnalyticsRelayOptions Options(Action<AnalyticsRelayOptions> configure)
    {
        var options = new AnalyticsRelayOptions();
        configure(options);
        return options;
    }

    [TestMethod]
    public async Task StateReturnsTheRegimeAndTheAnswer()
    {
        var (context, _, install) = Request();
        install.RequiresPriorConsent = true;

        await AnalyticsRelayExtensions.State(context);

        context.Response.Body.Position = 0;
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.AreEqual("""{"requiresPriorConsent":true,"consentAnswer":null}""", json);
    }

    [TestMethod]
    public async Task ConsentSetsTheOpposition()
    {
        var (context, _, install) = Request("""{"accepted":false}""");

        await AnalyticsRelayExtensions.Consent(context, new AnalyticsRelayOptions());

        Assert.AreEqual(204, context.Response.StatusCode);
        Assert.IsTrue(install.IsOptedOut);
    }

    [TestMethod]
    public async Task AnUnreadableConsentIsABadRequest()
    {
        var (context, _, _) = Request("""{"accepted":"yes"}""");

        await AnalyticsRelayExtensions.Consent(context, new AnalyticsRelayOptions());

        Assert.AreEqual(400, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task DropsUnknownEventsAndKeysSilently()
    {
        var (context, recorder, _) = Request("""
            [{"name":"content_viewed","props":{"content_id":"r1","free_text":"x","nested":{}}},
             {"name":"unknown"},
             {"name":"favorite_added","props":{"content_id":"r1"}}]
            """);
        var options = Options(o =>
        {
            o.Event("content_viewed", "content_id");
            o.Event("favorite_added");
        });

        await AnalyticsRelayExtensions.Events(context, options);

        Assert.AreEqual(204, context.Response.StatusCode);
        Assert.HasCount(2, recorder.Tracked);
        CollectionAssert.AreEquivalent(new[] { "content_id" }, recorder.Tracked[0].Props!.Keys.ToArray());
        Assert.IsEmpty(recorder.Tracked[1].Props!, "content_id is not allowed on favorite_added");
    }

    [TestMethod]
    public async Task ValidatorsTransformOrDropOneProp()
    {
        var (context, recorder, _) = Request("""{"name":"content_viewed","props":{"duration_s":100000,"content_id":"bad"}}""");
        var options = Options(o =>
        {
            o.Prop("duration_s", p => new(Math.Clamp(Convert.ToDouble(p.Value), 0, 86400)));
            o.Prop("content_id", p => new((object?)null));
            o.Event("content_viewed", "duration_s", "content_id");
        });

        await AnalyticsRelayExtensions.Events(context, options);

        var props = recorder.Tracked.Single().Props!;
        Assert.AreEqual(86400d, props["duration_s"]);
        Assert.IsFalse(props.ContainsKey("content_id"));
    }

    [TestMethod]
    public async Task FixedPropsOverrideTheBrowserAndReachValidators()
    {
        var (context, recorder, _) = Request("""{"name":"route_shared","props":{"content_type":"poi","content_id":"r1"}}""");
        string? seenType = null;
        var options = Options(o =>
        {
            o.Prop("content_id", p =>
            {
                seenType = p.Props["content_type"] as string;
                return new(p.Value);
            });
            o.Event("route_shared", "content_id").With("content_type", "route");
        });

        await AnalyticsRelayExtensions.Events(context, options);

        Assert.AreEqual("route", seenType);
        Assert.AreEqual("route", recorder.Tracked.Single().Props!["content_type"]);
    }

    [TestMethod]
    public async Task CapsTheEventsPerRequest()
    {
        var events = string.Join(",", Enumerable.Repeat("""{"name":"screen_view"}""", 30));
        var (context, recorder, _) = Request($"[{events}]");
        var options = Options(o => o.Event("screen_view"));

        await AnalyticsRelayExtensions.Events(context, options);

        Assert.HasCount(20, recorder.Tracked);
    }

    [TestMethod]
    public async Task AnUnreadableBodyIsDroppedWith204()
    {
        var (context, recorder, _) = Request("not json");

        await AnalyticsRelayExtensions.Events(context, Options(o => o.Event("screen_view")));

        Assert.AreEqual(204, context.Response.StatusCode);
        Assert.IsEmpty(recorder.Tracked);
    }

    [TestMethod]
    public async Task ATooLargeBodyIsDropped()
    {
        var (context, recorder, _) = Request("{\"name\":\"screen_view\",\"props\":{\"x\":\"" + new string('a', 200) + "\"}}");
        var options = Options(o =>
        {
            o.MaxBodyBytes = 100;
            o.Event("screen_view");
        });

        await AnalyticsRelayExtensions.Events(context, options);

        Assert.IsEmpty(recorder.Tracked);
    }

    [TestMethod]
    public async Task EventsBeyondTheAddressCeilingAreDropped()
    {
        var options = Options(o =>
        {
            o.Event("a");
            o.MaxEventsPerAddress = 3;
        });

        async Task<int> Post(string address)
        {
            var (context, recorder, _) = Request("""[{"name":"a"},{"name":"a"}]""");
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address);
            await AnalyticsRelayExtensions.Events(context, options);
            Assert.AreEqual(204, context.Response.StatusCode);
            return recorder.Tracked.Count;
        }

        Assert.AreEqual(2, await Post("203.0.113.7"));
        Assert.AreEqual(1, await Post("203.0.113.7"));
        Assert.AreEqual(0, await Post("203.0.113.7"));
        Assert.AreEqual(2, await Post("203.0.113.8"));

        // One /64 is one visitor.
        Assert.AreEqual(2, await Post("2001:db8:1:2::1"));
        Assert.AreEqual(1, await Post("2001:db8:1:2:ffff::9"));
        Assert.AreEqual(2, await Post("2001:db8:1:3::1"));
    }

    [TestMethod]
    public async Task TheAddressCeilingEndsWithItsWindow()
    {
        var start = DateTimeOffset.UtcNow;
        var clock = new ManualClock(start);
        var options = Options(o =>
        {
            o.Event("a");
            o.MaxEventsPerAddress = 1;
            o.Time = clock;
        });

        async Task<int> Post()
        {
            var (context, recorder, _) = Request("""{"name":"a"}""");
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
            await AnalyticsRelayExtensions.Events(context, options);
            return recorder.Tracked.Count;
        }

        Assert.AreEqual(1, await Post());
        Assert.AreEqual(0, await Post());
        clock.Now = start.AddMinutes(1);
        Assert.AreEqual(1, await Post());
    }

    [TestMethod]
    public async Task ZeroDisablesTheAddressCeiling()
    {
        var options = Options(o =>
        {
            o.Event("a");
            o.MaxEventsPerAddress = 0;
            o.MaxEventsPerRequest = 50;
        });
        var (context, recorder, _) = Request("[" + string.Join(",", Enumerable.Repeat("""{"name":"a"}""", 40)) + "]");
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");

        await AnalyticsRelayExtensions.Events(context, options);

        Assert.HasCount(40, recorder.Tracked);
    }

    sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public void NullAnalyticsAnswersTheState()
    {
        IInstallContext install = NullAnalytics.Instance;

        Assert.IsFalse(install.RequiresPriorConsent);
        Assert.IsNotNull(JsonSerializer.Serialize(install.ConsentAnswer));
    }
}
