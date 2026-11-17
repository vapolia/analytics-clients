namespace Vapolia.Analytics.Client.Tests;

[TestClass]
public class OptionsTests
{
    [TestMethod]
    public void OutOfRangeOptionsAreReplaced()
    {
        var options = new AnalyticsAdvancedOptions
        {
            FlushInterval = TimeSpan.Zero,
            RateWindow = TimeSpan.FromSeconds(-1),
            RequestTimeout = TimeSpan.Zero,
            BatchSize = 500,
            QueueCapacity = 0,
            MaxAttempts = 0,
            SpoolCapacity = -1,
        };

        options.Normalize(null);

        Assert.AreEqual(TimeSpan.FromSeconds(30), options.FlushInterval);
        Assert.AreEqual(TimeSpan.FromMinutes(1), options.RateWindow);
        Assert.AreEqual(TimeSpan.FromSeconds(10), options.RequestTimeout);
        Assert.AreEqual(100, options.BatchSize);
        Assert.AreEqual(4000, options.QueueCapacity);
        Assert.AreEqual(1, options.MaxAttempts);
        Assert.AreEqual(0, options.SpoolCapacity);
    }

    [TestMethod]
    public void DroppedPropsAreReported()
    {
        var dropped = new List<string>();
        var props = new Dictionary<string, object?>
        {
            ["ok"] = "win",
            ["id"] = Guid.NewGuid(),
            ["at"] = new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.FromHours(1)),
            ["day"] = new DateOnly(2026, 11, 16),
            ["hour"] = new TimeOnly(9, 30),
            ["size"] = new Version(1, 2),
            ["ratio"] = double.NaN,
            ["absent"] = null,
            ["blank"] = " ",
        };

        var kept = Clean.Props(props, dropped: (key, reason) => dropped.Add($"{key}: {reason}"));

        CollectionAssert.AreEquivalent(new[] { "ok", "id", "at", "day", "hour" }, kept!.Keys.ToArray());
        Assert.AreEqual("2026-11-16T09:30:00.000Z", kept["at"].StringValue);
        Assert.AreEqual("2026-11-16", kept["day"].StringValue);
        Assert.AreEqual("09:30:00", kept["hour"].StringValue);
        CollectionAssert.AreEquivalent(new[] { "size: unsupported type Version", "ratio: not a finite number" }, dropped);
    }

    [TestMethod]
    public void KeysOverTheCapAreReportedOnce()
    {
        var dropped = new List<string>();
        var props = Enumerable.Range(0, 5).ToDictionary(i => $"k{i}", i => (object?)i);

        var kept = Clean.Props(props, maxKeys: 3, dropped: (_, reason) => dropped.Add(reason));

        Assert.AreEqual(3, kept!.Count);
        CollectionAssert.AreEqual(new[] { "more than 3 keys" }, dropped);
    }

    [TestMethod]
    public void ValidOptionsAreKept()
    {
        var options = new AnalyticsAdvancedOptions { FlushInterval = TimeSpan.FromMilliseconds(50), BatchSize = 1, MaxAttempts = 7 };

        options.Normalize(null);

        Assert.AreEqual(TimeSpan.FromMilliseconds(50), options.FlushInterval);
        Assert.AreEqual(1, options.BatchSize);
        Assert.AreEqual(7, options.MaxAttempts);
    }
}
