using System.Text.Json;
using Vapolia.Analytics.Client;

namespace Vapolia.Analytics.Client.Tests;

[TestClass]
public class BatchEncoderTests
{
    const string InstallId = "11111111-0000-0000-0000-000011111111";

    static JsonElement Encode(Device device, params Event[] events)
        => JsonDocument.Parse(BatchEncoder.Encode(new BatchKey(InstallId, device), events)).RootElement;

    [TestMethod]
    public void EncodesTheShapeTheCollectorExpects()
    {
        var batch = Encode(
            new Device { Country = "FR" },
            new Event("app_open", DateTimeOffset.UnixEpoch, null),
            new Event("game_end", DateTimeOffset.UnixEpoch.AddSeconds(1), new Dictionary<string, PropValue>
            {
                ["result"] = PropValue.From("win"),
                ["moves"] = PropValue.From(34d),
            }));

        Assert.AreEqual(InstallId, batch.GetProperty("installId").GetString());
        Assert.AreEqual("FR", batch.GetProperty("country").GetString());
        // The batch describes the installation, never the device it runs on.
        Assert.IsFalse(batch.TryGetProperty("platform", out _));
        Assert.IsFalse(batch.TryGetProperty("build", out _));
        Assert.IsFalse(batch.TryGetProperty("osVersion", out _));
        Assert.IsFalse(batch.TryGetProperty("deviceClass", out _));
        Assert.IsFalse(batch.TryGetProperty("locale", out _));
        Assert.IsFalse(batch.TryGetProperty("store", out _));

        var events = batch.GetProperty("events");
        Assert.AreEqual(2, events.GetArrayLength());
        Assert.IsFalse(events[0].TryGetProperty("props", out _));
        Assert.AreEqual("win", events[1].GetProperty("props").GetProperty("result").GetString());
    }

    [TestMethod]
    public void TimestampsAreIso8601InUtc()
    {
        var batch = Encode(new Device(), new Event("app_open", DateTimeOffset.UnixEpoch, null));

        Assert.AreEqual("1970-01-01T00:00:00.000Z", batch.GetProperty("events")[0].GetProperty("ts").GetString());
    }

    [TestMethod]
    public void TimestampsAreConvertedToUtc()
    {
        var local = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.FromHours(2));

        var batch = Encode(new Device(), new Event("app_open", local, null));

        Assert.AreEqual("2026-09-12T10:00:00.000Z", batch.GetProperty("events")[0].GetProperty("ts").GetString());
    }

    [TestMethod]
    public void EventsAreOrderedByTimestamp()
    {
        var batch = Encode(
            new Device(),
            new Event("second", DateTimeOffset.UnixEpoch.AddSeconds(200), null),
            new Event("first", DateTimeOffset.UnixEpoch.AddSeconds(100), null));

        var events = batch.GetProperty("events");
        Assert.AreEqual("first", events[0].GetProperty("name").GetString());
        Assert.AreEqual("second", events[1].GetProperty("name").GetString());
    }

    [TestMethod]
    public void WholeNumbersKeepNoFractionalPart()
    {
        var batch = Encode(new Device(), new Event("game_end", DateTimeOffset.UnixEpoch, new Dictionary<string, PropValue>
        {
            ["moves"] = PropValue.From(34d),
            ["ratio"] = PropValue.From(0.5d),
        }));

        var props = batch.GetProperty("events")[0].GetProperty("props");
        Assert.AreEqual("34", props.GetProperty("moves").GetRawText());
        Assert.AreEqual(0.5d, props.GetProperty("ratio").GetDouble());
    }

    [TestMethod]
    public void StringsAreEscaped()
    {
        var batch = Encode(new Device(), new Event("screen_view", DateTimeOffset.UnixEpoch, new Dictionary<string, PropValue>
        {
            ["name"] = PropValue.From("a\"b\\c\nd"),
        }));

        Assert.AreEqual("a\"b\\c\nd", batch.GetProperty("events")[0].GetProperty("props").GetProperty("name").GetString());
    }
}
