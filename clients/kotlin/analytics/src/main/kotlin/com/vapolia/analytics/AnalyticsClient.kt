package com.vapolia.analytics

import java.util.concurrent.CountDownLatch
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * Queues events and sends them from one background thread. Safe for concurrent use.
 *
 * [Analytics] is the entry point an app uses; this class exists on its own so the whole send path can
 * be tested on the JVM, without an emulator.
 */
internal class AnalyticsClient(
    private val config: AnalyticsConfig,
    private val transport: Transport,
    private val spool: Spool?,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private sealed interface Command {
        class Add(val pending: Pending) : Command
        class Flush(val done: CountDownLatch, val persist: Boolean) : Command
        class Clear(val done: CountDownLatch) : Command
        class Stop(val done: CountDownLatch) : Command
    }

    private val commands = LinkedBlockingQueue<Command>()

    /** Events held by the client: queued, buffered, or waiting for a retry. Bounds the memory. */
    private val held = AtomicInteger()

    private val accepted = AtomicLong()
    private val rejected = AtomicLong()
    private val dropped = AtomicLong()
    private val sent = AtomicLong()
    private val requests = AtomicLong()

    private var windowStart = 0L
    private var windowCount = 0

    @Volatile private var stopping = false
    private val stopSignal = CountDownLatch(1)

    private val worker = Thread(::run, "vapolia-analytics").apply {
        isDaemon = true
        start()
    }

    /**
     * Queues one event. Never blocks, never throws, never reports an error. [stats] is where losses
     * show up.
     */
    fun track(
        installId: String,
        device: Device,
        name: String,
        props: Map<String, Any?>?,
        context: String = "{}",
    ) {
        if (!withinRate()) {
            dropped.incrementAndGet()
            return
        }

        val id = Clean.installId(installId)
        val eventName = Clean.text(name, MAX_VALUE_LENGTH)
        val cleanDevice = device.clean(config.excludedCountries)
        if (id == null || eventName == null || cleanDevice == null) {
            rejected.incrementAndGet()
            return
        }

        if (held.get() >= config.queueCapacity) {
            // Full queue: the collector is unreachable, or slower than we emit.
            dropped.incrementAndGet()
            return
        }

        held.incrementAndGet()
        accepted.incrementAndGet()
        val now = clock()
        commands.add(
            Command.Add(
                Pending(
                    BatchKey(id, cleanDevice, context),
                    // Minutes east of UTC at that instant, so a DST change between two events is not smoothed over.
                    Event(eventName, now, Clean.props(props), java.util.TimeZone.getDefault().getOffset(now) / 60_000),
                )
            )
        )
    }

    /**
     * The client's own ceiling, counted in a fixed window: once the window is full everything is
     * dropped until it ends, rather than queued for a collector that would refuse the whole address.
     */
    @Synchronized
    private fun withinRate(): Boolean {
        if (config.maxEventsPerWindow <= 0) return true

        val now = clock()
        if (now - windowStart >= config.rateWindowMs) {
            windowStart = now
            windowCount = 0
        }

        if (windowCount >= config.maxEventsPerWindow) return false

        windowCount++
        return true
    }

    /** Sends what is queued. [persist] writes whatever could not be sent to disk. */
    fun flush(timeoutMs: Long, persist: Boolean = false): Boolean {
        if (stopping) return false
        val done = CountDownLatch(1)
        commands.add(Command.Flush(done, persist))
        return done.await(timeoutMs, TimeUnit.MILLISECONDS)
    }

    /** Drops everything held and the spool. Used when the user opts out. */
    fun clear(timeoutMs: Long): Boolean {
        if (stopping) return false
        val done = CountDownLatch(1)
        commands.add(Command.Clear(done))
        return done.await(timeoutMs, TimeUnit.MILLISECONDS)
    }

    /** One last flush, then the sender stops. What it could not send is spooled. */
    fun stop(timeoutMs: Long): Boolean {
        if (stopping) return worker.state == Thread.State.TERMINATED
        val done = CountDownLatch(1)
        commands.add(Command.Stop(done))
        return done.await(timeoutMs, TimeUnit.MILLISECONDS)
    }

    fun stats() = AnalyticsStats(
        accepted = accepted.get(),
        rejected = rejected.get(),
        dropped = dropped.get(),
        sent = sent.get(),
        requests = requests.get(),
    )

    // The sender thread owns every buffer, so nothing below needs a lock. Sending happens here too:
    // the command queue is the buffer, and a single sender keeps one installation's requests in order.
    private fun run() {
        val buffers = LinkedHashMap<BatchKey, MutableList<Event>>()
        val encoder = BatchEncoder(config.buildToken)

        spool?.let { disk ->
            runCatching { disk.load() }
                .getOrDefault(emptyList())
                .forEach { buffer(buffers, it, counted = false) }
        }

        var nextFlush = clock() + config.flushIntervalMs
        while (true) {
            val wait = (nextFlush - clock()).coerceAtLeast(0)
            when (val command = commands.poll(wait, TimeUnit.MILLISECONDS)) {
                null -> {
                    flushAll(buffers, encoder)
                    nextFlush = clock() + config.flushIntervalMs
                }

                is Command.Add -> {
                    val events = buffer(buffers, command.pending, counted = true)
                    if (events.size >= config.batchSize) {
                        buffers.remove(command.pending.key)
                        keep(buffers, command.pending.key, send(command.pending.key, events, encoder))
                    }
                }

                is Command.Flush -> {
                    drain(buffers)
                    flushAll(buffers, encoder)
                    if (command.persist) persist(buffers)
                    command.done.countDown()
                    nextFlush = clock() + config.flushIntervalMs
                }

                is Command.Clear -> {
                    drain(buffers)
                    held.addAndGet(-buffers.values.sumOf { it.size })
                    buffers.clear()
                    runCatching { spool?.clear() }
                    command.done.countDown()
                }

                is Command.Stop -> {
                    // Retries stop sleeping from here on: a shutdown is not the moment to wait 5s.
                    stopping = true
                    stopSignal.countDown()
                    drain(buffers)
                    flushAll(buffers, encoder)
                    persist(buffers)
                    command.done.countDown()
                    return
                }
            }
        }
    }

    /** Moves what `track` already accepted into the buffers, so a flush covers it. */
    private fun drain(buffers: MutableMap<BatchKey, MutableList<Event>>) {
        val deferred = ArrayList<Command>()
        while (true) {
            val command = commands.poll() ?: break
            if (command is Command.Add)
                buffer(buffers, command.pending, counted = true)
            else
                deferred.add(command) // answered by the loop, after this one is
        }
        commands.addAll(deferred)
    }

    private fun buffer(
        buffers: MutableMap<BatchKey, MutableList<Event>>,
        pending: Pending,
        counted: Boolean,
    ): MutableList<Event> {
        if (!counted) held.incrementAndGet() // reloaded from disk: never counted by track
        return buffers.getOrPut(pending.key) { ArrayList() }.apply { add(pending.event) }
    }

    private fun flushAll(
        buffers: MutableMap<BatchKey, MutableList<Event>>,
        encoder: BatchEncoder,
    ) {
        if (buffers.isEmpty()) return

        val groups = buffers.toList()
        buffers.clear()
        for ((key, events) in groups) {
            for (chunk in events.chunked(config.batchSize))
                keep(buffers, key, send(key, chunk, encoder))
        }
    }

    /** Puts back what a transient failure left unsent, unless the client is already at its ceiling. */
    private fun keep(
        buffers: MutableMap<BatchKey, MutableList<Event>>,
        key: BatchKey,
        events: List<Event>,
    ) {
        if (events.isEmpty()) return

        val room = config.queueCapacity - buffers.values.sumOf { it.size }
        val kept = if (events.size > room) events.take(room.coerceAtLeast(0)) else events
        if (kept.size < events.size) {
            val lost = events.size - kept.size
            dropped.addAndGet(lost.toLong())
            held.addAndGet(-lost)
        }
        if (kept.isNotEmpty())
            buffers.getOrPut(key) { ArrayList() }.addAll(kept)
    }

    /** Sends one group and accounts for it. Returns the events to try again later, if any. */
    private fun send(key: BatchKey, events: List<Event>, encoder: BatchEncoder): List<Event> {
        val cutoff = clock() - MAX_EVENT_AGE_MS
        val fresh = events.filter { it.tsMillis > cutoff }
        val stale = events.size - fresh.size
        if (stale > 0) {
            // The collector refuses them on arrival, so this only saves the request.
            dropped.addAndGet(stale.toLong())
            held.addAndGet(-stale)
        }
        if (fresh.isEmpty()) return emptyList()

        val body = try {
            encoder.encode(key, fresh).toByteArray(Charsets.UTF_8)
        } catch (e: Exception) {
            discard(fresh, "encoding ${fresh.size} events", e)
            return emptyList()
        }

        var attempt = 1
        while (true) {
            requests.incrementAndGet()
            when (val result = transport.post(body)) {
                is SendResult.Ok -> {
                    sent.addAndGet(fresh.size.toLong())
                    held.addAndGet(-fresh.size)
                    return emptyList()
                }

                is SendResult.Permanent -> {
                    discard(fresh, "dropping ${fresh.size} events: ${result.reason}", null)
                    return emptyList()
                }

                is SendResult.Retry -> {
                    if (attempt >= config.maxAttempts || !sleep(backoff(attempt, result.afterMs))) {
                        // Kept, not dropped: a failed send on a phone usually means no network.
                        config.logger?.warn("analytics: keeping ${fresh.size} events: ${result.reason}")
                        return fresh
                    }
                    config.logger?.warn("analytics: retrying ${fresh.size} events: ${result.reason}")
                    attempt++
                }
            }
        }
    }

    private fun discard(events: List<Event>, message: String, error: Throwable?) {
        dropped.addAndGet(events.size.toLong())
        held.addAndGet(-events.size)
        config.logger?.error("analytics: $message", error)
    }

    private fun persist(buffers: Map<BatchKey, MutableList<Event>>) {
        val disk = spool ?: return
        val items = buffers.flatMap { (key, events) -> events.map { Pending(key, it) } }
        runCatching { disk.save(items) }
            .onFailure { config.logger?.error("analytics: cannot spool ${items.size} events", it) }
    }

    /** Waits, and reports false if the client is stopping. */
    private fun sleep(durationMs: Long): Boolean {
        if (stopping) return false
        return !stopSignal.await(durationMs, TimeUnit.MILLISECONDS)
    }

    private fun backoff(attempt: Int, retryAfterMs: Long): Long {
        val base = if (retryAfterMs > 0) retryAfterMs else 250L shl (attempt - 1)
        return base.coerceAtMost(MAX_RETRY_DELAY_MS)
    }

    private companion object {
        /** The sender shares the thread that drains the queue, so a long sleep is paid in drops. */
        const val MAX_RETRY_DELAY_MS = 5_000L
    }
}
