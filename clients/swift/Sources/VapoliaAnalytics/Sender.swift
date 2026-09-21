import Foundation

/// Buffers events and sends them, one group per request.
///
/// Every mutation goes through one command queue drained by one consumer task, which is the only
/// thing that ever touches `buffers`. That is what makes `track` then `flush` observe the events in
/// the order they were produced: an actor alone would not, because each caller would arrive in its
/// own task. The Kotlin client's `LinkedBlockingQueue<Command>` and the Go client's `queue chan
/// pending` are the same design.
actor Sender {
    private let options: AnalyticsOptions
    private let poster: HTTPPoster
    private let spool: Spool?
    private let counters: Counters
    private let now: @Sendable () -> Date
    private let timestamps = BatchEncoder.makeTimestampFormatter()

    private var buffers: [BatchKey: [Event]] = [:]
    private var stopped = false
    private var warnedAboutGroups = false

    private let commands: AsyncStream<Command>
    private let feed: AsyncStream<Command>.Continuation
    private var ticker: Task<Void, Never>?

    /// `done` is what a caller waits on. Nil for a tracked event, which never blocks its caller.
    private enum Command: Sendable {
        case start(done: (@Sendable () -> Void)?)
        case event(Pending, done: (@Sendable () -> Void)?)
        case flush(persist: Bool, done: (@Sendable () -> Void)?)
        case clear(done: (@Sendable () -> Void)?)
        case stop(done: (@Sendable () -> Void)?)
    }

    init(
        options: AnalyticsOptions,
        poster: HTTPPoster,
        spool: Spool?,
        counters: Counters,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.options = options
        self.poster = poster
        self.spool = spool
        self.counters = counters
        self.now = now

        var feed: AsyncStream<Command>.Continuation!
        commands = AsyncStream(bufferingPolicy: .unbounded) { feed = $0 }
        self.feed = feed

        // The consumer starts here, not in `start()`: a caller may post before it, and the commands
        // would otherwise pile up unanswered.
        Task { await self.consume() }
    }

    // MARK: - Posting

    /// Queues one event without waiting for it. Called from `track`, on whatever thread it runs on:
    /// yielding to the stream is non-blocking and thread-safe, which is what `track` promises.
    nonisolated func enqueue(_ pending: Pending) {
        feed.yield(.event(pending, done: nil))
    }

    /// Reads back what the last run could not send, then starts the periodic flush.
    func start() async {
        await post { .start(done: $0) }
    }

    /// Buffers one event and waits for it to be buffered.
    func add(_ pending: Pending) async {
        await post { .event(pending, done: $0) }
    }

    /// Sends every buffered group. `persist` writes down whatever could not be sent.
    func flush(persist: Bool) async {
        await post { .flush(persist: persist, done: $0) }
    }

    /// Drops everything held, on disk included. Used when the user opts out.
    func clear() async {
        await post { .clear(done: $0) }
    }

    /// One last flush, then the sender stops. What it could not send is left on disk.
    func stop() async {
        await post { .stop(done: $0) }
    }

    private func post(_ make: @escaping (@escaping @Sendable () -> Void) -> Command) async {
        await withCheckedContinuation { continuation in
            feed.yield(make { continuation.resume() })
        }
    }

    // MARK: - The one consumer

    private func consume() async {
        for await command in commands {
            switch command {
            case .start(let done):
                performStart()
                done?()

            case .event(let pending, let done):
                await buffer(pending)
                done?()

            case .flush(let persist, let done):
                await performFlush(persist: persist)
                done?()

            case .clear(let done):
                performClear()
                done?()

            case .stop(let done):
                await performStop()
                done?()
                feed.finish()
            }
        }
    }

    private func performStart() {
        if let spool {
            let items = spool.load()
            if !items.isEmpty {
                counters.reserveUncounted(items.count)
                for item in items {
                    buffers[item.key, default: []].append(item.event)
                }
            }
        }

        let interval = UInt64(max(1, options.advanced.flushInterval) * 1_000_000_000)
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: interval)
                if Task.isCancelled { return }
                await self?.flush(persist: false)
            }
        }
    }

    /// Buffers one event, and sends its group as soon as the group is a full batch.
    private func buffer(_ pending: Pending) async {
        guard !stopped else {
            // Its slot was reserved by `track`; nothing will send it now.
            counters.released(1)
            return
        }

        buffers[pending.key, default: []].append(pending.event)
        boundGroups()

        guard let events = buffers[pending.key], events.count >= options.advanced.batchSize else { return }

        buffers.removeValue(forKey: pending.key)
        keep(await send(key: pending.key, events: events), for: pending.key)
    }

    /// A batch context that changes on every event would make one group, and so one request, per
    /// event. Flushing is the graceful answer: it empties the groups without losing anything.
    private func boundGroups() {
        guard buffers.count > Limits.maxBufferedGroups else { return }

        if !warnedAboutGroups {
            warnedAboutGroups = true
            options.advanced.logger?.warn(
                "\(buffers.count) batch contexts buffered at once: a context that changes on every "
                    + "event costs one request per event. Keep Analytics.context to stable buckets."
            )
        }

        Task { [weak self] in await self?.flush(persist: false) }
    }

    private func performFlush(persist: Bool) async {
        let groups = buffers
        buffers.removeAll(keepingCapacity: true)

        for (key, events) in groups {
            for chunk in stride(from: 0, to: events.count, by: options.advanced.batchSize) {
                let slice = Array(events[chunk..<min(chunk + options.advanced.batchSize, events.count)])
                keep(await send(key: key, events: slice), for: key)
            }
        }

        if persist { persistBuffers() }
    }

    private func performClear() {
        counters.released(buffers.values.reduce(0) { $0 + $1.count })
        buffers.removeAll()
        spool?.clear()
    }

    private func performStop() async {
        stopped = true
        ticker?.cancel()
        ticker = nil
        await performFlush(persist: true)
        poster.shutdown()
    }

    // MARK: - Sending

    /// Sends one group and accounts for it. Returns the events to try again later, if any.
    private func send(key: BatchKey, events: [Event]) async -> [Event] {
        let cutoff = now().addingTimeInterval(-Limits.maxEventAge)
        let fresh = events.filter { $0.ts > cutoff }
        if fresh.count < events.count {
            // The collector refuses them on arrival, so this only saves the request.
            let stale = events.count - fresh.count
            counters.dropped(stale)
            report(nil, "dropping \(stale) events older than the collector accepts", permanent: true)
        }
        guard !fresh.isEmpty else { return [] }

        let body: Data
        do {
            body = try BatchEncoder.encode(key: key, events: fresh, timestamps: timestamps)
        } catch {
            counters.dropped(fresh.count)
            report(error, "cannot encode \(fresh.count) events", permanent: true)
            return []
        }

        var attempt = 1
        while true {
            counters.requested()
            switch await poster.post(body) {
            case .ok:
                counters.sent(fresh.count)
                return []

            case .permanent(let reason):
                counters.dropped(fresh.count)
                report(nil, "dropping \(fresh.count) events: \(reason)", permanent: true)
                return []

            case .retry(let after, let reason):
                if attempt >= options.advanced.maxAttempts || stopped {
                    // Kept, not dropped: a failed send on a phone usually means no network.
                    options.advanced.logger?.warn("keeping \(fresh.count) events: \(reason)")
                    return fresh
                }

                options.advanced.logger?.warn("retrying \(fresh.count) events: \(reason)")
                let delay = after > 0 ? after : 0.25 * pow(2, Double(attempt - 1))
                try? await Task.sleep(nanoseconds: UInt64(min(delay, 5) * 1_000_000_000))
                attempt += 1
            }
        }
    }

    /// Puts back what a transient failure left unsent, unless the client is already at its ceiling.
    ///
    /// An opt-out racing this needs no guard of its own: `clear` is a command, so the one consumer
    /// runs it strictly after the flush that is in flight, and empties what this put back.
    private func keep(_ events: [Event], for key: BatchKey) {
        guard !events.isEmpty else { return }

        let room = options.advanced.queueCapacity - buffers.values.reduce(0) { $0 + $1.count }
        let kept = events.count > room ? Array(events.prefix(max(0, room))) : events
        if kept.count < events.count {
            let lost = events.count - kept.count
            counters.dropped(lost)
            report(nil, "dropping \(lost) events: the queue is full", permanent: false)
        }
        if !kept.isEmpty {
            buffers[key, default: []].append(contentsOf: kept)
        }
    }

    /// Every loss reaches the log and, when the app asked for one, `onError` — which is the hook a
    /// client uses to tell its user that measurement is degraded.
    private func report(_ error: Error?, _ reason: String, permanent: Bool) {
        if permanent {
            options.advanced.logger?.error(reason)
        } else {
            options.advanced.logger?.warn(reason)
        }
        options.advanced.onError?(error, reason, permanent)
    }

    private func persistBuffers() {
        guard let spool else { return }

        // Sorted by timestamp because `buffers` is a Dictionary: its iteration order is the hash
        // order, so without this the spool's cap would keep an arbitrary subset rather than the
        // oldest events, which are the ones a relaunch is meant to recover.
        let items = buffers
            .flatMap { key, events in events.map { Pending(key: key, event: $0) } }
            .sorted { $0.event.ts < $1.event.ts }

        let discarded = spool.save(items)
        if discarded > 0 {
            counters.dropped(discarded)
            report(nil, "dropping \(discarded) events: the spool is full", permanent: false)
        }
    }
}
