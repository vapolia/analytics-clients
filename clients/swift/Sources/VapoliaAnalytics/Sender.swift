import Foundation

/// Buffers events and sends them, one group per request. The actor is the serialization: nothing
/// here is called from two places at once, so no buffer needs a lock.
actor Sender {
    private let config: AnalyticsConfig
    private let poster: HTTPPoster
    private let spool: Spool?
    private let counters: Counters
    private let now: @Sendable () -> Date
    private let timestamps = BatchEncoder.makeTimestampFormatter()

    private var buffers: [BatchKey: [Event]] = [:]
    private var ticker: Task<Void, Never>?
    private var stopped = false

    init(
        config: AnalyticsConfig,
        poster: HTTPPoster,
        spool: Spool?,
        counters: Counters,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.config = config
        self.poster = poster
        self.spool = spool
        self.counters = counters
        self.now = now
    }

    /// Reads back what the last run could not send, then starts the periodic flush.
    func start() {
        if let spool {
            let items = spool.load()
            if !items.isEmpty {
                counters.reserveUncounted(items.count)
                for item in items {
                    buffers[item.key, default: []].append(item.event)
                }
            }
        }

        let interval = UInt64(max(1, config.flushInterval) * 1_000_000_000)
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: interval)
                if Task.isCancelled { return }
                await self?.flush(persist: false)
            }
        }
    }

    /// Buffers one event, and sends its group as soon as the group is a full batch.
    func add(_ pending: Pending) async {
        guard !stopped else { return }

        buffers[pending.key, default: []].append(pending.event)
        guard let events = buffers[pending.key], events.count >= config.batchSize else { return }

        buffers.removeValue(forKey: pending.key)
        keep(await send(key: pending.key, events: events), for: pending.key)
    }

    /// Sends every buffered group. `persist` writes down whatever could not be sent.
    func flush(persist: Bool) async {
        let groups = buffers
        buffers.removeAll(keepingCapacity: true)

        for (key, events) in groups {
            for chunk in stride(from: 0, to: events.count, by: config.batchSize) {
                let slice = Array(events[chunk..<min(chunk + config.batchSize, events.count)])
                keep(await send(key: key, events: slice), for: key)
            }
        }

        if persist { persistBuffers() }
    }

    /// Drops everything held, on disk included. Used when the user opts out.
    func clear() {
        counters.released(buffers.values.reduce(0) { $0 + $1.count })
        buffers.removeAll()
        spool?.clear()
    }

    /// One last flush, then the sender stops. What it could not send is left on disk.
    func stop() async {
        stopped = true
        ticker?.cancel()
        ticker = nil
        await flush(persist: true)
    }

    // MARK: - Sending

    /// Sends one group and accounts for it. Returns the events to try again later, if any.
    private func send(key: BatchKey, events: [Event]) async -> [Event] {
        let cutoff = now().addingTimeInterval(-Limits.maxEventAge)
        let fresh = events.filter { $0.ts > cutoff }
        if fresh.count < events.count {
            // The collector refuses them on arrival, so this only saves the request.
            counters.dropped(events.count - fresh.count)
        }
        guard !fresh.isEmpty else { return [] }

        let body: Data
        do {
            body = try BatchEncoder.encode(key: key, events: fresh, timestamps: timestamps, buildToken: config.buildToken)
        } catch {
            counters.dropped(fresh.count)
            config.logger?.error("cannot encode \(fresh.count) events: \(error)")
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
                config.logger?.error("dropping \(fresh.count) events: \(reason)")
                return []

            case .retry(let after, let reason):
                if attempt >= config.maxAttempts || stopped {
                    // Kept, not dropped: a failed send on a phone usually means no network.
                    config.logger?.warn("keeping \(fresh.count) events: \(reason)")
                    return fresh
                }

                config.logger?.warn("retrying \(fresh.count) events: \(reason)")
                let delay = after > 0 ? after : 0.25 * pow(2, Double(attempt - 1))
                try? await Task.sleep(nanoseconds: UInt64(min(delay, 5) * 1_000_000_000))
                attempt += 1
            }
        }
    }

    /// Puts back what a transient failure left unsent, unless the client is already at its ceiling.
    private func keep(_ events: [Event], for key: BatchKey) {
        guard !events.isEmpty else { return }

        let room = config.queueCapacity - buffers.values.reduce(0) { $0 + $1.count }
        let kept = events.count > room ? Array(events.prefix(max(0, room))) : events
        if kept.count < events.count {
            counters.dropped(events.count - kept.count)
        }
        if !kept.isEmpty {
            buffers[key, default: []].append(contentsOf: kept)
        }
    }

    private func persistBuffers() {
        guard let spool else { return }

        let items = buffers.flatMap { key, events in
            events.map { Pending(key: key, event: $0) }
        }
        spool.save(items)
    }
}
