using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vapolia.Analytics.Client.Tests;

/// <summary>The collector, reduced to what a client can observe: the bodies, and what it answered.</summary>
sealed class FakeCollector(params SendResult[] answers) : IPublishHelper
{
    readonly Queue<SendResult> queued = new(answers);
    readonly Lock gate = new();
    readonly List<string> bodies = [];

    public IReadOnlyList<string> Bodies
    {
        get { lock (gate) return bodies.ToList(); }
    }

    public IReadOnlyList<JsonElement> Batches
        => Bodies.Select(b => JsonDocument.Parse(b).RootElement).ToList();

    public Task<SendResult> PostAsync(string body, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            bodies.Add(body);
            return Task.FromResult(queued.Count > 0 ? queued.Dequeue() : SendResult.Ok);
        }
    }
}

[TestClass]
public class SenderTests
{
    const string InstallId = "11111111-0000-0000-0000-000011111111";
    const string OtherInstallId = "22222222-0000-0000-0000-000022222222";

    static AnalyticsOptions Options(int batchSize = 100, int maxAttempts = 3, int queueCapacity = 4000) => new()
    {
        IngestionUrl = new Uri("https://localhost/testsource"),
        AdvancedOptions = {
            // Long enough that every test flushes explicitly.
            FlushInterval = TimeSpan.FromHours(1),
            BatchSize = batchSize,
            MaxAttempts = maxAttempts,
            QueueCapacity = queueCapacity,
        }
    };

    static Pending Item(string name, string installId = InstallId, Device? device = null, DateTimeOffset? ts = null)
        => new(
            new BatchKey(installId, device ?? new Device { Country = "FR" }),
            new Event(name, ts ?? DateTimeOffset.UtcNow, null));

    [TestMethod]
    public async Task SendsOneBatchCarryingTheGlobalProperties()
    {
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(), collector, null, NullLogger.Instance);
        var device = new Device { Country = "FR" };

        sender.Track(Item("app_open", device: device));
        sender.Track(Item("game_end", device: device));
        await sender.FlushAsync();

        Assert.AreEqual(1, collector.Bodies.Count);
        var batch = collector.Batches[0];
        Assert.AreEqual(InstallId, batch.GetProperty("installId").GetString());
        Assert.AreEqual("FR", batch.GetProperty("country").GetString());
        Assert.AreEqual(2, batch.GetProperty("events").GetArrayLength());
        Assert.AreEqual(2, sender.Stats.Sent);
    }

    [TestMethod]
    public async Task GroupsByInstallIdAndDevice()
    {
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(), collector, null, NullLogger.Instance);

        sender.Track(Item("app_open"));
        sender.Track(Item("game_start"));
        sender.Track(Item("app_open", device: new Device { Country = "BE" }));
        sender.Track(Item("app_open", installId: OtherInstallId));
        await sender.FlushAsync();

        Assert.AreEqual(3, collector.Bodies.Count);
    }

    [TestMethod]
    public async Task SendsAsSoonAsABatchIsFull()
    {
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(batchSize: 3), collector, null, NullLogger.Instance);

        for (var index = 0; index < 3; index++)
            sender.Track(Item("app_open"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (collector.Bodies.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.AreEqual(1, collector.Bodies.Count);
        Assert.AreEqual(3, collector.Batches[0].GetProperty("events").GetArrayLength());
    }

    [TestMethod]
    public async Task KeepsEventsATransientFailureLeftUnsent()
    {
        var collector = new FakeCollector(
            SendResult.Retry(TimeSpan.Zero, "500"),
            SendResult.Retry(TimeSpan.Zero, "500"));
        await using var sender = new Sender(Options(maxAttempts: 2), collector, null, NullLogger.Instance);

        sender.Track(Item("app_open"));
        await sender.FlushAsync();

        Assert.AreEqual(2, sender.Stats.Requests);
        Assert.AreEqual(0, sender.Stats.Sent);
        Assert.AreEqual(0, sender.Stats.Dropped, "a client without network keeps its events");

        // The next flush finds the collector back and sends the same event.
        await sender.FlushAsync();
        Assert.AreEqual(1, sender.Stats.Sent);
    }

    [TestMethod]
    public async Task DropsWhatARetryCannotFix()
    {
        var collector = new FakeCollector(SendResult.Permanent("unknown source (404)"));
        await using var sender = new Sender(Options(), collector, null, NullLogger.Instance);

        sender.Track(Item("app_open"));
        await sender.FlushAsync();

        Assert.AreEqual(1, collector.Bodies.Count, "a permanent refusal is not retried");
        Assert.AreEqual(1, sender.Stats.Dropped);
    }

    [TestMethod]
    public async Task DropsEventsOlderThanTheCollectorAccepts()
    {
        var collector = new FakeCollector();
        await using var sender = new Sender(Options(), collector, null, NullLogger.Instance);

        sender.Track(Item("app_open", ts: DateTimeOffset.UtcNow - Clean.MaxEventAge - TimeSpan.FromHours(1)));
        await sender.FlushAsync();

        Assert.AreEqual(0, collector.Bodies.Count);
        Assert.AreEqual(1, sender.Stats.Dropped);
    }

    [TestMethod]
    public async Task AFullQueueDropsInsteadOfBlocking()
    {
        // Nothing is ever acknowledged, so the queue stays full for the whole test.
        var collector = new FakeCollector(Enumerable.Repeat(SendResult.Retry(TimeSpan.Zero, "offline"), 64).ToArray());
        await using var sender = new Sender(Options(maxAttempts: 1, queueCapacity: 1), collector, null, NullLogger.Instance);

        for (var index = 0; index < 200; index++)
            sender.Track(Item("app_open"));

        var stats = sender.Stats;
        Assert.AreEqual(200, stats.Accepted + stats.Dropped);
        Assert.IsTrue(stats.Dropped >= 198, $"expected nearly everything dropped, got {stats.Dropped}");
    }

    [TestMethod]
    public async Task WhatCouldNotBeSentIsSpooledAndSentOnTheNextStart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vapolia-spool-{Guid.NewGuid():N}.json");
        try
        {
            var failing = new FakeCollector(SendResult.Retry(TimeSpan.Zero, "offline"));
            var first = new Sender(Options(maxAttempts: 1), failing, new (path, 100, null), NullLogger.Instance);
            first.Track(Item("game_end"));
            await first.FlushAsync(persist: true);
            await first.DisposeAsync();

            Assert.IsTrue(File.Exists(path), "the queue should have been written down");

            var collector = new FakeCollector();
            await using var restarted = new Sender(Options(), collector, new (path, 100, null), NullLogger.Instance);
            await restarted.FlushAsync();

            Assert.AreEqual(1, collector.Bodies.Count);
            Assert.AreEqual("game_end", collector.Batches[0].GetProperty("events")[0].GetProperty("name").GetString());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestMethod]
    public async Task PurgeForgetsQueuedAndRetainedEventsOfOneInstallation()
    {
        var collector = new FakeCollector(SendResult.Retry(TimeSpan.Zero, "offline"));
        await using var sender = new Sender(Options(maxAttempts: 1), collector, null, NullLogger.Instance);

        // One retained after a failed send, one still queued.
        sender.Track(Item("app_open"));
        await sender.FlushAsync();
        sender.Track(Item("game_end"));
        sender.Track(Item("app_open", installId: OtherInstallId));

        sender.Purge(InstallId);
        await sender.FlushAsync();

        Assert.AreEqual(2, collector.Bodies.Count);
        Assert.AreEqual(OtherInstallId, collector.Batches[1].GetProperty("installId").GetString());
        Assert.AreEqual(2, sender.Stats.Dropped);
        Assert.AreEqual(1, sender.Stats.Sent);
    }

    [TestMethod]
    public async Task PurgeEmptiesTheSpool()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vapolia-spool-{Guid.NewGuid():N}.json");
        try
        {
            var collector = new FakeCollector(SendResult.Retry(TimeSpan.Zero, "offline"));
            await using var sender = new Sender(Options(maxAttempts: 1), collector, new(path, 100, null), NullLogger.Instance);
            sender.Track(Item("game_end"));
            await sender.FlushAsync(persist: true);
            Assert.IsTrue(File.Exists(path));

            sender.Purge();
            await sender.FlushAsync();

            Assert.IsFalse(File.Exists(path), "an opposition must not leave events on disk");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
