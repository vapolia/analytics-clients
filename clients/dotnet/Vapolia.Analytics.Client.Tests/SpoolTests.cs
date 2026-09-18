using Vapolia.Analytics.Client;

namespace Vapolia.Analytics.Client.Tests;

[TestClass]
public class SpoolTests
{
    string path = "";

    [TestInitialize]
    public void CreateTemporaryPath()
        => path = Path.Combine(Path.GetTempPath(), $"vapolia-spool-{Guid.NewGuid():N}.json");

    [TestCleanup]
    public void RemoveTemporaryPath()
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    static Pending Item(string name) => new(
        new BatchKey(
            "11111111-0000-0000-0000-000011111111",
            new Device { Country = "FR" },
            """{"plan":"premium"}"""),
        new Event(name, DateTimeOffset.UnixEpoch.AddSeconds(1), new Dictionary<string, PropValue>
        {
            ["result"] = PropValue.From("win"),
            ["moves"] = PropValue.From(34d),
            ["ok"] = PropValue.From(true),
        }));

    [TestMethod]
    public void RoundTripsEventsDeviceContextIncluded()
    {
        var spool = new Spool(path, 10);
        var items = new[] { Item("app_open"), Item("game_end") };

        spool.Save(items);
        var loaded = spool.Load();

        Assert.AreEqual(2, loaded.Count);
        Assert.AreEqual("app_open", loaded[0].Event.Name);
        Assert.AreEqual("FR", loaded[0].Key.Device.Country);
        // The batch context is part of the key: a restart must not merge two different contexts.
        Assert.AreEqual("""{"plan":"premium"}""", loaded[0].Key.Context);
        Assert.AreEqual(items[0].Event.Ts, loaded[0].Event.Ts);
        Assert.AreEqual(PropValue.From("win"), loaded[0].Event.Props!["result"]);
        Assert.AreEqual(PropValue.From(34d), loaded[0].Event.Props!["moves"]);
        Assert.AreEqual(PropValue.From(true), loaded[0].Event.Props!["ok"]);
    }

    [TestMethod]
    public void IsReadOnlyOnce()
    {
        var spool = new Spool(path, 10);
        spool.Save([Item("app_open")]);

        Assert.AreEqual(1, spool.Load().Count);
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(0, spool.Load().Count);
    }

    [TestMethod]
    public void SavingNothingClearsTheFile()
    {
        var spool = new Spool(path, 10);
        spool.Save([Item("app_open")]);

        spool.Save([]);

        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void ATruncatedFileYieldsNothing()
    {
        var spool = new Spool(path, 10);
        spool.Save([Item("app_open"), Item("game_end")]);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        Assert.AreEqual(0, spool.Load().Count);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void CapacityKeepsTheOldestEvents()
    {
        var spool = new Spool(path, 2);

        spool.Save([Item("first"), Item("second"), Item("third")]);

        CollectionAssert.AreEqual(
            new[] { "first", "second" },
            spool.Load().Select(i => i.Event.Name).ToArray());
    }
}
