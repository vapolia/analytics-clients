using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Vapolia.Analytics.Client;

/// <summary>
/// Buffers events, groups them per (install id, device), and sends them from one background loop.
///
/// The channel is the queue and the flush signal both, so the loop owns every buffer and nothing here
/// needs a lock.
/// </summary>
sealed class Sender : IAsyncDisposable
{
    readonly record struct Command(Pending? Item, TaskCompletionSource? Ack, bool Persist);

    readonly AnalyticsOptions options;
    readonly IPublishHelper publishHelper;
    readonly PersistPendingItemsToLocalStorageHelper? spool;
    readonly ILogger logger;
    readonly TimeProvider time;

    readonly Channel<Command> channel;
    readonly CancellationTokenSource stopping = new();
    readonly Task loop;

    long accepted, rejected, dropped, sent, requests;
    int held;

    readonly Lock rateGate = new();
    DateTimeOffset windowStart;
    int windowCount;

    public Sender(AnalyticsOptions options, IPublishHelper publishHelper, PersistPendingItemsToLocalStorageHelper? spool, ILogger logger, TimeProvider? time = null)
    {
        this.options = options;
        this.publishHelper = publishHelper;
        this.spool = spool;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;

        channel = Channel.CreateBounded<Command>(new BoundedChannelOptions(options.AdvancedOptions.QueueCapacity)
        {
            // Never block a caller: Track uses TryWrite and counts the refusal itself.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        loop = Task.Run(RunAsync);
    }

    public AnalyticsStats Stats => new(
        Interlocked.Read(ref accepted),
        Interlocked.Read(ref rejected),
        Interlocked.Read(ref dropped),
        Interlocked.Read(ref sent),
        Interlocked.Read(ref requests));

    public void Reject() => Interlocked.Increment(ref rejected);

    /// <summary>Queues one event. Never blocks, never throws: a refusal is only a counter.</summary>
    public void Track(Pending pending)
    {
        if (!WithinRate())
        {
            Interlocked.Increment(ref dropped);
            return;
        }

        if (Volatile.Read(ref held) >= options.AdvancedOptions.QueueCapacity || !channel.Writer.TryWrite(new Command(pending, null, false)))
        {
            // Full queue: the collector is unreachable, or slower than we emit.
            Interlocked.Increment(ref dropped);
            return;
        }

        Interlocked.Increment(ref held);
        Interlocked.Increment(ref accepted);
    }

    /// <summary>Sends what is queued and waits for it.</summary>
    public async Task FlushAsync(bool persist = false, CancellationToken cancellationToken = default)
    {
        if (stopping.IsCancellationRequested)
            return;

        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await channel.Writer.WriteAsync(new Command(null, ack, persist), cancellationToken).ConfigureAwait(false);
        await ack.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task RunAsync()
    {
        var buffers = new Dictionary<BatchKey, List<Event>>();

        foreach (var item in spool?.Load() ?? [])
        {
            Buffer(buffers, item);
            Interlocked.Increment(ref held);
        }

        using var timer = new PeriodicTimer(options.AdvancedOptions.FlushInterval, time);

        // Both tasks are hoisted out of the loop and only recreated once they have completed: a fresh
        // WaitToReadAsync on every tick would leave a continuation behind on a channel that is never
        // completed, one per flush, for the life of the process.
        Task<bool>? read = null;
        Task? tick = null;

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                read ??= channel.Reader.WaitToReadAsync(stopping.Token).AsTask();
                tick ??= TickAsync(timer);

                if (await Task.WhenAny(read, tick).ConfigureAwait(false) == tick)
                {
                    tick = null;
                    await FlushAllAsync(buffers).ConfigureAwait(false);
                    continue;
                }

                var available = await read.ConfigureAwait(false);
                read = null;
                if (!available)
                    break;

                while (channel.Reader.TryRead(out var command))
                {
                    if (command.Item is { } pending)
                    {
                        Buffer(buffers, pending);
                        if (buffers[pending.Key].Count >= options.AdvancedOptions.BatchSize)
                            await SendGroupAsync(buffers, pending.Key).ConfigureAwait(false);
                        continue;
                    }

                    await FlushAllAsync(buffers).ConfigureAwait(false);
                    if (command.Persist)
                        Persist(buffers);
                    command.Ack?.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        finally
        {
            // Drain what Track already accepted, then write down what could not leave.
            while (channel.Reader.TryRead(out var command))
            {
                if (command.Item is { } pending)
                    Buffer(buffers, pending);
                else
                    command.Ack?.TrySetResult();
            }

            Persist(buffers);
        }
    }

    static async Task TickAsync(PeriodicTimer timer)
    {
        try
        {
            await timer.WaitForNextTickAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The timer is disposed with the loop.
        }
    }

    static void Buffer(Dictionary<BatchKey, List<Event>> buffers, Pending pending)
    {
        if (!buffers.TryGetValue(pending.Key, out var events))
            buffers[pending.Key] = events = [];

        events.Add(pending.Event);
    }

    async Task FlushAllAsync(Dictionary<BatchKey, List<Event>> buffers)
    {
        foreach (var key in buffers.Keys.ToList())
            await SendGroupAsync(buffers, key).ConfigureAwait(false);
    }

    async Task SendGroupAsync(Dictionary<BatchKey, List<Event>> buffers, BatchKey key)
    {
        if (!buffers.Remove(key, out var events))
            return;

        for (var index = 0; index < events.Count; index += options.AdvancedOptions.BatchSize)
        {
            var chunk = events.GetRange(index, Math.Min(options.AdvancedOptions.BatchSize, events.Count - index));
            var kept = await SendAsync(key, chunk).ConfigureAwait(false);
            if (kept.Count == 0)
                continue;

            if (!buffers.TryGetValue(key, out var existing))
                buffers[key] = existing = [];
            existing.AddRange(kept);
        }
    }

    /// <summary>Sends one group and accounts for it. Returns the events to try again later, if any.</summary>
    async Task<List<Event>> SendAsync(BatchKey key, List<Event> events)
    {
        // A send started as the app goes to the background is killed mid-request unless the platform has been told to hold the process.
        IAsyncDisposable? scope = null;
        if (options.AppOptions.BackgroundScope is { } open)
        {
            try
            {
                scope = await open("analytics").ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "analytics: background scope refused");
            }
        }

        try
        {
            return await SendCoreAsync(key, events).ConfigureAwait(false);
        }
        finally
        {
            if (scope is not null)
                await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    async Task<List<Event>> SendCoreAsync(BatchKey key, List<Event> events)
    {
        var cutoff = time.GetUtcNow() - Clean.MaxEventAge;
        var fresh = events.Where(e => e.Ts > cutoff).ToList();
        if (fresh.Count < events.Count)
        {
            // The collector refuses them on arrival, so this only saves the request.
            Drop(events.Count - fresh.Count);
        }

        if (fresh.Count == 0)
            return [];

        var body = BatchEncoder.Encode(key, fresh, options.AccessToken);

        for (var attempt = 1; ; attempt++)
        {
            Interlocked.Increment(ref requests);

            SendResult result;
            try
            {
                result = await publishHelper.PostAsync(body, stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down: keep them for the spool rather than spending the last moments retrying.
                return fresh;
            }

            switch (result.Outcome)
            {
                case SendOutcome.Ok:
                    Interlocked.Add(ref sent, fresh.Count);
                    Interlocked.Add(ref held, -fresh.Count);
                    return [];

                case SendOutcome.Permanent:
                    logger.LogError("analytics: dropping {Count} events: {Reason}", fresh.Count, result.Reason);
                    Report(result.Exception, result.Reason, permanent: true);
                    Drop(fresh.Count);
                    return [];

                default:
                    if (attempt >= options.AdvancedOptions.MaxAttempts || stopping.IsCancellationRequested)
                    {
                        // Kept, not dropped: a failed send usually means no network.
                        logger.LogWarning("analytics: keeping {Count} events: {Reason}", fresh.Count, result.Reason);
                        Report(result.Exception, result.Reason, permanent: false);
                        return fresh;
                    }

                    logger.LogWarning("analytics: retrying {Count} events: {Reason}", fresh.Count, result.Reason);
                    var delay = result.RetryAfter > TimeSpan.Zero
                        ? result.RetryAfter
                        : TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));

                    try
                    {
                        await Task.Delay(delay > MaxRetryDelay ? MaxRetryDelay : delay, time, stopping.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return fresh;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The client's own ceiling, counted in a fixed window: once the window is full everything is
    /// dropped until it ends, rather than queued for a collector that would refuse the whole address.
    /// </summary>
    bool WithinRate()
    {
        if (options.AdvancedOptions.MaxEventsPerWindow <= 0)
            return true;

        var now = time.GetUtcNow();
        lock (rateGate)
        {
            if (now - windowStart >= options.AdvancedOptions.RateWindow)
            {
                windowStart = now;
                windowCount = 0;
            }

            if (windowCount >= options.AdvancedOptions.MaxEventsPerWindow)
            {
                // Once per window, not per dropped event: a saturated window is one fact. Silence
                // here is how a chatty tutorial session is discovered months later, in the numbers.
                if (windowCount == options.AdvancedOptions.MaxEventsPerWindow)
                {
                    windowCount++;
                    logger.LogWarning("analytics: rate window full, dropping until it ends");
                    Report(null, $"client rate window full ({options.AdvancedOptions.MaxEventsPerWindow} per {options.AdvancedOptions.RateWindow})", permanent: false);
                }

                return false;
            }

            windowCount++;
            return true;
        }
    }

    /// <summary>Next to the log, for an app that reports losses somewhere of its own.</summary>
    void Report(Exception? exception, string reason, bool permanent)
    {
        if (options.AdvancedOptions.OnError is not { } report)
            return;

        try
        {
            report(exception, reason, permanent);
        }
        catch (Exception e)
        {
            // A reporting callback that throws must not take the send loop down with it.
            logger.LogWarning(e, "analytics: OnError threw");
        }
    }

    void Drop(int count)
    {
        Interlocked.Add(ref dropped, count);
        Interlocked.Add(ref held, -count);
    }

    void Persist(Dictionary<BatchKey, List<Event>> buffers)
    {
        if (spool is null)
            return;

        var items = buffers.SelectMany(pair => pair.Value.Select(e => new Pending(pair.Key, e))).ToList();
        spool.Save(items);
    }

    /// <summary>The sender shares the loop that drains the queue, so a long sleep is paid in drops.</summary>
    static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);

    public async ValueTask DisposeAsync()
    {
        channel.Writer.TryComplete();

        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The spool already holds what mattered.
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            stopping.Dispose();
        }
    }
}
