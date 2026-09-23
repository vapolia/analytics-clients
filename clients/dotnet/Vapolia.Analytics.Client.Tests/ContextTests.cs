using Microsoft.Extensions.Logging.Abstractions;
using Vapolia.Analytics.Client;

namespace Vapolia.Analytics.Client.Tests;

enum Difficulty
{
    Easy,
    Hard,
}

[TestClass]
public class EnumPropTests
{
    [TestMethod]
    public void EnumsAreStoredByName()
    {
        var props = Clean.Props(new Dictionary<string, object?> { ["difficulty"] = Difficulty.Hard })!;

        // Not 1: a query filters on the name, and reordering the enum would silently rewrite history.
        Assert.AreEqual("\"Hard\"", System.Text.Json.JsonSerializer.Serialize(props["difficulty"]));
    }

    [TestMethod]
    public void EnumsSurviveTheNumericCase()
    {
        // An int-backed enum matches IConvertible too; the enum case has to come first.
        var props = Clean.Props(new Dictionary<string, object?> { ["difficulty"] = Difficulty.Easy })!;
        Assert.AreEqual(1, props.Count);
        Assert.AreEqual("\"Easy\"", System.Text.Json.JsonSerializer.Serialize(props["difficulty"]));
    }
}

[TestClass]
public class InstallAgeTests
{
    [TestMethod]
    public void BucketsAreTheAgeOfTheInstallation()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.AreEqual("0", InstallAge.Bucket(now.AddHours(-3), now));
        Assert.AreEqual("1-7", InstallAge.Bucket(now.AddDays(-1), now));
        Assert.AreEqual("1-7", InstallAge.Bucket(now.AddDays(-7), now));
        Assert.AreEqual("8-30", InstallAge.Bucket(now.AddDays(-8), now));
        Assert.AreEqual("31-90", InstallAge.Bucket(now.AddDays(-31), now));
        Assert.AreEqual("90+", InstallAge.Bucket(now.AddDays(-400), now));
    }
}

[TestClass]
public class OnErrorTests
{
    const string InstallId = "11111111-0000-0000-0000-000011111111";

    static Pending Item(DateTimeOffset ts) => new(
        new BatchKey(InstallId, new Device { Country = "FR" }),
        new Event("app_open", ts, null));

    [TestMethod]
    public async Task APermanentRefusalIsReportedAsPermanent()
    {
        var losses = new List<(string Reason, bool Permanent)>();
        var options = new AnalyticsOptions
        {
            IngestionUrl = new Uri("https://localhost/testsource"),
            AdvancedOptions =
            {
                FlushInterval = TimeSpan.FromHours(1),
                MaxEventsPerWindow = 0,
                OnError = (_, reason, permanent) => losses.Add((reason, permanent)),
            }
        };

        var collector = new FakeCollector(SendResult.Permanent("unknown source (404)"));
        await using var sender = new Sender(options, collector, null, NullLogger.Instance);

        sender.Track(Item(DateTimeOffset.UtcNow));
        await sender.FlushAsync();

        Assert.AreEqual(1, losses.Count);
        Assert.IsTrue(losses[0].Permanent, "a 404 is not something the next flush fixes");
        Assert.AreEqual(1, sender.Stats.Dropped);
    }

    [TestMethod]
    public async Task GivingUpAfterRetriesIsReportedAsTransient()
    {
        var losses = new List<(string Reason, bool Permanent)>();
        var options = new AnalyticsOptions
        {
            IngestionUrl = new Uri("https://localhost/testsource"),
            AdvancedOptions = {
                FlushInterval = TimeSpan.FromHours(1),
                MaxEventsPerWindow = 0,
                MaxAttempts = 2,
                OnError = (_, reason, permanent) => losses.Add((reason, permanent)),
            }
        };

        var collector = new FakeCollector(
            SendResult.Retry(TimeSpan.Zero, "network error"),
            SendResult.Retry(TimeSpan.Zero, "network error"));
        await using var sender = new Sender(options, collector, null, NullLogger.Instance);

        sender.Track(Item(DateTimeOffset.UtcNow));
        await sender.FlushAsync();

        Assert.AreEqual(1, losses.Count);
        Assert.IsFalse(losses[0].Permanent, "kept for the next flush, not dropped");
        Assert.AreEqual(0, sender.Stats.Dropped);
    }

    [TestMethod]
    public async Task TheExceptionItselfIsReported()
    {
        var reported = new List<Exception?>();
        var options = new AnalyticsOptions
        {
            IngestionUrl = new Uri("https://localhost/testsource"),
            AdvancedOptions = 
            {
                FlushInterval = TimeSpan.FromHours(1),
                MaxEventsPerWindow = 0,
                MaxAttempts = 1,
                OnError = (exception, _, _) => reported.Add(exception),
            }
        };

        var failure = new HttpRequestException("connection refused");
        var collector = new FakeCollector(SendResult.Retry(TimeSpan.Zero, "network error", failure));
        await using var sender = new Sender(options, collector, null, NullLogger.Instance);

        sender.Track(Item(DateTimeOffset.UtcNow));
        await sender.FlushAsync();

        // Sentry groups on the type and the stack; a message string would make every failure its own issue.
        Assert.AreEqual(1, reported.Count);
        Assert.AreSame(failure, reported[0]);
    }

    [TestMethod]
    public async Task ASaturatedWindowIsReportedOnce()
    {
        var losses = new List<string>();
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var options = new AnalyticsOptions
        {
            IngestionUrl = new Uri("https://localhost/testsource"),
            AdvancedOptions = 
            {
                FlushInterval = TimeSpan.FromHours(1),
                MaxEventsPerWindow = 2,
                RateWindow = TimeSpan.FromMinutes(1),
                OnError = (_, reason, _) => losses.Add(reason),
            }
        };

        await using var sender = new Sender(options, new FakeCollector(), null, NullLogger.Instance, clock);

        for (var index = 0; index < 10; index++)
            sender.Track(Item(clock.GetUtcNow()));

        // Eight events were dropped, but a full window is one fact, not eight.
        Assert.AreEqual(1, losses.Count);
        Assert.Contains("rate window full", losses[0]);
    }
}

/// <summary>
/// The batch context replaced the three named business fields. It is the app's dictionary, cleaned
/// like props, and part of what groups events into one request.
/// </summary>
[TestClass]
public class BatchContextTests
{
    const string InstallId = "11111111-0000-0000-0000-000011111111";

    sealed class Identity : IInstallContext
    {
        public string GetInstallId() => InstallId;
        public Device GetDevice() => new() { Country = "FR" };
        public bool IsOptedOut { get; set; }
    }

    sealed class Context(Func<IReadOnlyDictionary<string, object?>?> current) : IAnalyticsContext
    {
        public IReadOnlyDictionary<string, object?>? GetContext() => current();
    }

    static AnalyticsOptions Options() => new()
    {
        IngestionUrl = new Uri("https://localhost/testsource"),
        AdvancedOptions = {
            FlushInterval = TimeSpan.FromHours(1),
            MaxEventsPerWindow = 0,
        }
    };

    [TestMethod]
    public async Task TravelsOnTheBatchNotOnTheEvent()
    {
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(), collector, null, NullLogger.Instance);
        var analytics = new Analytics(sender, new Identity(), new Context(() =>
            new Dictionary<string, object?> { ["plan"] = "premium", ["tutorial_done"] = true }));

        analytics.Track("app_open");
        await analytics.FlushAsync();

        var batch = collector.Batches[0];
        Assert.AreEqual("premium", batch.GetProperty("context").GetProperty("plan").GetString());
        Assert.IsTrue(batch.GetProperty("context").GetProperty("tutorial_done").GetBoolean());
    }

    [TestMethod]
    public async Task AChangedContextStartsAnotherBatch()
    {
        var premium = false;
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(), collector, null, NullLogger.Instance);
        var analytics = new Analytics(sender, new Identity(), new Context(() =>
            new Dictionary<string, object?> { ["plan"] = premium ? "premium" : "free" }));

        analytics.Track("app_open");
        premium = true;
        analytics.Track("game_end");
        await analytics.FlushAsync();

        // An event carries the state it was produced under, so the two cannot share a batch.
        Assert.AreEqual(2, collector.Bodies.Count);
        Assert.AreEqual("free", collector.Batches[0].GetProperty("context").GetProperty("plan").GetString());
        Assert.AreEqual("premium", collector.Batches[1].GetProperty("context").GetProperty("plan").GetString());
    }

    [TestMethod]
    public async Task AnUnchangedContextGroupsIntoOneBatch()
    {
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(), collector, null, NullLogger.Instance);
        var analytics = new Analytics(sender, new Identity(), new Context(() =>
            new Dictionary<string, object?> { ["tutorial_done"] = true, ["plan"] = "free" }));

        analytics.Track("app_open");
        analytics.Track("game_end");
        await analytics.FlushAsync();

        // Same pairs, whatever order the dictionary hands them over: the key is canonical.
        Assert.AreEqual(1, collector.Bodies.Count);
    }

    [TestMethod]
    public async Task NoContextSendsNoContext()
    {
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(), collector, null, NullLogger.Instance);
        var analytics = new Analytics(sender, new Identity());

        analytics.Track("app_open");
        await analytics.FlushAsync();

        Assert.IsFalse(collector.Batches[0].TryGetProperty("context", out _));
    }
}

/// <summary>tz travels with each event, apart from ts which stays UTC, from whoever knows the offset.</summary>
[TestClass]
public class TimeZoneTests
{
    const string InstallId = "11111111-0000-0000-0000-000011111111";

    sealed class Identity : IInstallContext
    {
        public string GetInstallId() => InstallId;
        public Device GetDevice() => new() { Country = "FR" };
        public bool IsOptedOut { get; set; }
    }

    sealed class FixedZone(int? minutes) : IAnalyticsTimeZone
    {
        public DateTimeOffset? GetLocalTime(DateTimeOffset at)
            => minutes is { } m ? at.ToOffset(TimeSpan.FromMinutes(m)) : null;
    }

    static AnalyticsOptions Options() => new()
    {
        IngestionUrl = new Uri("https://localhost/testsource"),
        AdvancedOptions = {
            FlushInterval = TimeSpan.FromHours(1),
            MaxEventsPerWindow = 0,
        }
    };

    static async Task<System.Text.Json.JsonElement> SendOne(IAnalyticsTimeZone? zone)
    {
        var options = Options();
        var collector = new FakeCollector();
        await using var sender = new Sender(options, collector, null, NullLogger.Instance);
        var analytics = new Analytics(sender, new Identity(), null, options, zone);

        analytics.Track("app_open");
        await analytics.FlushAsync();

        return collector.Batches[0].GetProperty("events")[0];
    }

    [TestMethod]
    public async Task SendsTheOffsetInMinutesWithEachEvent()
    {
        var sent = await SendOne(new FixedZone(120));

        Assert.AreEqual(120, sent.GetProperty("tz").GetInt32());
        // ts stays UTC: the offset is never folded into it.
        StringAssert.EndsWith(sent.GetProperty("ts").GetString(), "Z");
    }

    [TestMethod]
    public async Task NoTimeZoneSendsNoTz()
    {
        var sent = await SendOne(null);

        Assert.IsFalse(sent.TryGetProperty("tz", out _));
    }

    [TestMethod]
    public async Task AZoneThatKnowsNothingSendsNoTz()
    {
        var sent = await SendOne(new FixedZone(null));

        Assert.IsFalse(sent.TryGetProperty("tz", out _));
    }

    [TestMethod]
    public async Task AnOffsetOutsideTheRealRangeIsNotSent()
    {
        // DateTimeOffset allows ±14h, the inhabited range stops at -12.
        var sent = await SendOne(new FixedZone(-13 * 60));

        Assert.IsFalse(sent.TryGetProperty("tz", out _));
    }
}
