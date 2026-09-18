import Foundation

/// Where transport failures go. Nothing else is ever reported: see ``Analytics/track(_:_:)``.
public protocol AnalyticsLogger: Sendable {
    func warn(_ message: String)
    func error(_ message: String)
}

/// Prints the client's own failures. Pass it as ``AnalyticsConfig/logger`` while integrating.
public struct PrintLogger: AnalyticsLogger {
    public init() {}
    public func warn(_ message: String) { print("[analytics] \(message)") }
    public func error(_ message: String) { print("[analytics] \(message)") }
}

/// Everything the client needs beyond the app's identity. ``source`` and ``endpoint`` have no default.
public struct AnalyticsConfig: Sendable {
    /// The app's name: the URL segment, and the Postgres schema it maps to.
    public var source: String

    /// The collector's base URL, e.g. "https://analytics.example.com". The source is appended to it.
    public var endpoint: String

    /// How often pending events are sent even when no batch is full.
    public var flushInterval: TimeInterval

    /// Events of one (install, device) group per request. Capped at the collector's own ceiling.
    public var batchSize: Int

    /// Events waiting for the sender. Beyond it ``Analytics/track(_:_:)`` drops rather than blocks.
    public var queueCapacity: Int

    /// Events accepted per ``rateWindow``. Beyond it everything is dropped until the window ends. It
    /// keeps a buggy installation from getting its whole client address throttled by the collector,
    /// which behind a carrier NAT would hit every other installation too. Zero disables it.
    public var maxEventsPerWindow: Int

    /// The window ``maxEventsPerWindow`` counts in. Fixed, not sliding.
    public var rateWindow: TimeInterval

    /// Events kept on disk across process death.
    public var spoolCapacity: Int

    /// Attempts per request before a batch is given up on (kept for later if the failure was transient).
    public var maxAttempts: Int

    public var requestTimeout: TimeInterval

    /// The token issued for this build by the collector's admin service, as a CI step, and embedded in
    /// the app. It replaces the platform and build number in every batch, so a build cannot be invented
    /// and can be excluded. Public by nature, not a credential. A source that requires one answers 401
    /// without.
    public var buildToken: String?

    /// ISO 3166-1 alpha-2 countries not measured at all: nothing is sent from a device whose region is
    /// one of them. Copy the source's `excludedCountries`.
    public var excludedCountries: Set<String>

    /// Optional. Nothing is logged when nil.
    public var logger: AnalyticsLogger?

    public init(
        source: String,
        endpoint: String,
        flushInterval: TimeInterval = 30,
        batchSize: Int = Limits.maxEventsPerBatch,
        queueCapacity: Int = 2_000,
        maxEventsPerWindow: Int = 30,
        rateWindow: TimeInterval = 60,
        spoolCapacity: Int = 1_000,
        maxAttempts: Int = 3,
        requestTimeout: TimeInterval = 10,
        buildToken: String? = nil,
        excludedCountries: Set<String> = [],
        logger: AnalyticsLogger? = nil
    ) {
        self.source = source
        self.endpoint = endpoint
        self.flushInterval = flushInterval
        self.batchSize = min(batchSize, Limits.maxEventsPerBatch)
        self.queueCapacity = queueCapacity
        self.maxEventsPerWindow = maxEventsPerWindow
        self.rateWindow = rateWindow
        self.spoolCapacity = spoolCapacity
        self.maxAttempts = maxAttempts
        self.requestTimeout = requestTimeout
        self.buildToken = buildToken
        self.excludedCountries = Set(excludedCountries.map { $0.uppercased() })
        self.logger = logger
    }
}

/// A snapshot of what the client did since it was created.
public struct AnalyticsStats: Equatable, Sendable {
    /// Queued by `track`.
    public var accepted: Int = 0
    /// Refused by `track`: opted out, bad install id, excluded country, unusable event name.
    public var rejected: Int = 0
    /// Accepted then lost: full queue, full spool, or a permanently refused request.
    public var dropped: Int = 0
    /// Events the collector answered 2xx for.
    public var sent: Int = 0
    /// Requests issued, retries included.
    public var requests: Int = 0
}

/// The counters, and the queue's ceiling.
///
/// They are shared by the call site (which counts what it accepts and refuses, synchronously) and the
/// sender actor (which counts what leaves), so they live behind a lock rather than inside the actor.
final class Counters: @unchecked Sendable {
    private let lock = NSLock()
    private var stats = AnalyticsStats()

    /// Events held by the client: queued, buffered, or waiting for a retry. Bounds the memory.
    private var held = 0

    var snapshot: AnalyticsStats {
        lock.lock()
        defer { lock.unlock() }
        return stats
    }

    private var windowStart: Date = .distantPast
    private var windowCount = 0

    /// The client's own ceiling, counted in a fixed window: once it is full everything is dropped
    /// until the window ends, rather than queued for a collector that would refuse the whole address.
    func withinRate(limit: Int, window: TimeInterval, now: Date) -> Bool {
        guard limit > 0 else { return true }

        lock.lock()
        defer { lock.unlock() }

        if now.timeIntervalSince(windowStart) >= window {
            windowStart = now
            windowCount = 0
        }

        guard windowCount < limit else {
            stats.dropped += 1
            return false
        }

        windowCount += 1
        return true
    }

    /// Takes one slot in the queue, or reports that it is full.
    func reserve(limit: Int) -> Bool {
        lock.lock()
        defer { lock.unlock() }

        guard held < limit else {
            // Full queue: the collector is unreachable, or slower than we emit.
            stats.dropped += 1
            return false
        }

        held += 1
        stats.accepted += 1
        return true
    }

    /// Slots taken by events read back from disk, which `reserve` never saw.
    func reserveUncounted(_ count: Int) {
        lock.lock()
        defer { lock.unlock() }
        held += count
    }

    var heldCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return held
    }

    func rejected(_ count: Int = 1) {
        lock.lock()
        defer { lock.unlock() }
        stats.rejected += count
    }

    func sent(_ count: Int) {
        lock.lock()
        defer { lock.unlock() }
        stats.sent += count
        held -= count
    }

    func dropped(_ count: Int) {
        lock.lock()
        defer { lock.unlock() }
        stats.dropped += count
        held -= count
    }

    func released(_ count: Int) {
        lock.lock()
        defer { lock.unlock() }
        held -= count
    }

    func requested() {
        lock.lock()
        defer { lock.unlock() }
        stats.requests += 1
    }
}
