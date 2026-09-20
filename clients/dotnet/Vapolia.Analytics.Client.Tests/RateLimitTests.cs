using Microsoft.Extensions.Logging.Abstractions;

namespace Vapolia.Analytics.Client.Tests;

/// <summary>
/// A clock the test moves. Only <see cref="GetUtcNow"/> is overridden: the periodic flush keeps the
/// real timer, and every test below sets an interval long enough never to fire.
/// </summary>
sealed class MovableClock(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

[TestClass]
public class RateLimitTests
{
    const string InstallId = "11111111-0000-0000-0000-000011111111";

    static Pending Item(DateTimeOffset ts) => new(
        new BatchKey(InstallId, new Device { Country = "FR" }),
        new Event("app_open", ts, null));

    static AnalyticsOptions Options(int maxPerWindow) => new()
    {
        IngestionUrl = new Uri("https://localhost/testsource"),
        AdvancedOptions = {
            FlushInterval = TimeSpan.FromHours(1),
            MaxEventsPerWindow = maxPerWindow,
            RateWindow = TimeSpan.FromMinutes(1),
        }
    };

    [TestMethod]
    public async Task DropsEverythingPastTheCeilingUntilTheWindowEnds()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(3), collector, null, NullLogger.Instance, clock);

        for (var index = 0; index < 5; index++)
            sender.Track(Item(clock.GetUtcNow()));

        var stats = sender.Stats;
        Assert.AreEqual(3, stats.Accepted);
        Assert.AreEqual(2, stats.Dropped, "past the ceiling the window drops, it does not queue");

        // Still inside the same window: nothing gets through.
        clock.Advance(TimeSpan.FromSeconds(59));
        sender.Track(Item(clock.GetUtcNow()));
        Assert.AreEqual(3, sender.Stats.Accepted);

        // The window has ended: the count starts again.
        clock.Advance(TimeSpan.FromSeconds(2));
        sender.Track(Item(clock.GetUtcNow()));
        Assert.AreEqual(4, sender.Stats.Accepted);

        await sender.FlushAsync();
        Assert.AreEqual(4, sender.Stats.Sent);
    }

    [TestMethod]
    public async Task ZeroDisablesTheCeiling()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var collector = new FakeCollector();
        // What the server side registers: one process legitimately speaks for every visitor.
        await using var sender = new Sender(Options(0), collector, null, NullLogger.Instance, clock);

        for (var index = 0; index < 200; index++)
            sender.Track(Item(clock.GetUtcNow()));

        Assert.AreEqual(200, sender.Stats.Accepted);
        Assert.AreEqual(0, sender.Stats.Dropped);
    }

    [TestMethod]
    public async Task TheCeilingIsCountedPerWindowNotPerSend()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(2), collector, null, NullLogger.Instance, clock);

        sender.Track(Item(clock.GetUtcNow()));
        sender.Track(Item(clock.GetUtcNow()));
        await sender.FlushAsync();

        // Flushing does not refill the window: it is time that does.
        sender.Track(Item(clock.GetUtcNow()));
        Assert.AreEqual(2, sender.Stats.Accepted);
        Assert.AreEqual(1, sender.Stats.Dropped);
    }
}
