using Microsoft.Extensions.Logging.Abstractions;

namespace Vapolia.Analytics.Client.Tests;

/// <summary>What an app tracks while the consent question is unanswered goes out on an acceptance only.</summary>
[TestClass]
public class AwaitingConsentTests
{
    const string Id = "11111111-0000-0000-0000-000011111111";

    sealed class Identity : IInstallContext
    {
        public bool? Answer { get; set; }
        public string? InstallId => Answer == true ? Id : null;
        public string? Country => "DE";
        public bool IsOptedOut { get => Answer is not true; set => Answer = !value; }
        public bool? ConsentAnswer => Answer;
        public bool RequiresPriorConsent => true;
    }

    static AnalyticsOptions Options() => new()
    {
        IngestionUrl = new Uri("https://localhost/testsource"),
        AdvancedOptions = { FlushInterval = TimeSpan.FromHours(1), MaxEventsPerWindow = 0 },
    };

    [TestMethod]
    public async Task AnAcceptanceSendsWhatWasTrackedMeanwhile()
    {
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(), collector, null, NullLogger.Instance);
        var identity = new Identity();
        var analytics = new Analytics(sender, identity, holdsUnanswered: true);

        analytics.Track("first_open");
        analytics.Track("app_open");
        await analytics.FlushAsync();
        Assert.IsEmpty(collector.Bodies);

        identity.IsOptedOut = false;
        analytics.Answered(accepted: true);
        await analytics.FlushAsync();

        var events = collector.Batches[0].GetProperty("events").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        CollectionAssert.AreEqual(new[] { "first_open", "app_open" }, events);
    }

    [TestMethod]
    public async Task ARefusalDropsIt()
    {
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(), collector, null, NullLogger.Instance);
        var identity = new Identity();
        var analytics = new Analytics(sender, identity, holdsUnanswered: true);

        analytics.Track("app_open");
        identity.IsOptedOut = true;
        analytics.Answered(accepted: false);
        identity.IsOptedOut = false;
        analytics.Answered(accepted: true);
        await analytics.FlushAsync();

        Assert.IsEmpty(collector.Bodies);
    }
}
