import Foundation

/// Where transport failures go. Nothing else is ever reported: see ``Analytics/track(_:_:)``.
public protocol AnalyticsLogger: Sendable {
    func warn(_ message: String)
    func error(_ message: String)
}

/// Prints the client's own failures. Pass it as ``AnalyticsAdvancedOptions/logger`` while integrating.
public struct PrintLogger: AnalyticsLogger {
    public init() {}
    public func warn(_ message: String) { print("[analytics] \(message)") }
    public func error(_ message: String) { print("[analytics] \(message)") }
}

/// Client configuration. ``ingestionUrl`` is the only thing without a default.
///
/// The shape mirrors the .NET client, which is this repository's reference: the few options an app
/// actually sets sit here, the rest in ``advanced`` and ``app``.
public struct AnalyticsOptions: Sendable {
    /// The collector's ingestion URL — `https://baseUrl/sourceName`. The last path segment is the
    /// source: the app's name, and the Postgres schema it maps to.
    public var ingestionUrl: URL

    /// The credential sent as `Authorization: Bearer` — a server token for server-to-server
    /// analytics, or a build token for device-to-server analytics. The collector tells the two apart
    /// from the token itself, not from how it arrived, so one property covers both roles.
    public var token: String?

    /// The identity of this installation, when it comes from somewhere else — a client being
    /// migrated off another SDK. Read once, at startup, and only if nothing is stored yet.
    public var seedInstallId: (@Sendable () -> InstallSeed?)?

    /// True measures nothing at all: ``Analytics/start(_:)`` is a no-op, and nothing restarts the
    /// client before the process does. Set it from the build configuration. The person's own switch
    /// is ``Analytics/isOptedOut``, which takes effect at once and either way.
    public var isDebugBuild: Bool

    /// What ``Analytics/isOptedOut`` answers while the person has not answered. True sends nothing and
    /// writes no installation id until the welcome popup sets ``Analytics/isOptedOut`` to false.
    ///
    /// Nil reads the device locale and answers from ``localeRequiresPriorConsent(_:)``.
    public var requiresPriorConsent: Bool?

    /// ISO 3166-1 alpha-2 countries excluded from the collection. Should be a copy of the exclusion
    /// list of the collector (the analytics server).
    public var excludedCountries: Set<String>

    /// Context sent with each batch of events. Every key must be on the source's `context`
    /// whitelist. Seeds ``Analytics/context``, which can be set again later.
    public var context: (@Sendable () -> [String: PropValue]?)?

    /// Uncommon options.
    public var advanced: AnalyticsAdvancedOptions

    /// Uncommon apps-only options.
    public var app: AnalyticsAppOptions

    public init(
        ingestionUrl: URL,
        token: String? = nil,
        seedInstallId: (@Sendable () -> InstallSeed?)? = nil,
        isDebugBuild: Bool = false,
        requiresPriorConsent: Bool? = nil,
        excludedCountries: Set<String> = [],
        context: (@Sendable () -> [String: PropValue]?)? = nil,
        advanced: AnalyticsAdvancedOptions = .init(),
        app: AnalyticsAppOptions = .init()
    ) {
        self.ingestionUrl = ingestionUrl
        self.token = token
        self.seedInstallId = seedInstallId
        self.isDebugBuild = isDebugBuild
        self.requiresPriorConsent = requiresPriorConsent
        self.excludedCountries = Set(excludedCountries.map { $0.uppercased() })
        self.context = context
        self.advanced = advanced
        self.app = app
    }

    /// The app's name, which the collector reads as the schema to write into: the URL's last
    /// segment, exactly as in the .NET client.
    public var source: String { ingestionUrl.lastPathComponent }
}

/// Uncommon options. The defaults are the ones every client in this repository ships.
public struct AnalyticsAdvancedOptions: Sendable {
    /// How long events are buffered before they go out.
    public var flushInterval: TimeInterval

    /// Max events accepted per ``rateWindow``. Beyond it, everything is dropped until the window
    /// ends. Zero disables it. It keeps a buggy installation from getting its whole client address
    /// throttled by the collector, which behind a carrier NAT would hit every other installation.
    public var maxEventsPerWindow: Int

    /// The window ``maxEventsPerWindow`` counts in. Fixed, not sliding.
    public var rateWindow: TimeInterval

    /// Max events to send per batch. Capped at the collector's own ceiling.
    public var batchSize: Int

    /// Max events that can wait to be sent. Beyond it ``Analytics/track(_:_:)`` drops rather than
    /// blocks.
    public var queueCapacity: Int

    /// Max attempts to send a batch before it is given up on — kept for later when the failure was
    /// transient.
    public var maxAttempts: Int

    /// Timeout when posting analytics data to the collector.
    public var requestTimeout: TimeInterval

    /// Where the unsent queue is written down. Nil is the default location, in Caches: unsent
    /// counters have no business being backed up to iCloud and restored onto another device.
    public var spoolPath: URL?

    /// Events kept in the spool file. Zero disables the spool.
    public var spoolCapacity: Int

    /// 13 months minus a margin for clock drift — the legal ceiling, with no extension.
    public var installIdLifetime: TimeInterval

    /// How long a refusal is remembered. Unlike the identifier, it is refreshed on every visit.
    public var optOutLifetime: TimeInterval

    /// Optional. Nothing is logged when nil.
    public var logger: AnalyticsLogger?

    /// Called on every loss, next to the log: `(error, reason, permanent)`. Can be used to notify
    /// the user that an issue is ongoing.
    public var onError: (@Sendable (Error?, String, Bool) -> Void)?

    public init(
        flushInterval: TimeInterval = 30,
        maxEventsPerWindow: Int = 30,
        rateWindow: TimeInterval = 60,
        batchSize: Int = Limits.maxEventsPerBatch,
        queueCapacity: Int = 4_000,
        maxAttempts: Int = 3,
        requestTimeout: TimeInterval = 10,
        spoolPath: URL? = nil,
        spoolCapacity: Int = 1_000,
        installIdLifetime: TimeInterval = 390 * 24 * 60 * 60,
        optOutLifetime: TimeInterval = 390 * 24 * 60 * 60,
        logger: AnalyticsLogger? = nil,
        onError: (@Sendable (Error?, String, Bool) -> Void)? = nil
    ) {
        self.flushInterval = flushInterval
        self.maxEventsPerWindow = maxEventsPerWindow
        self.rateWindow = rateWindow
        self.batchSize = min(batchSize, Limits.maxEventsPerBatch)
        self.queueCapacity = queueCapacity
        self.maxAttempts = maxAttempts
        self.requestTimeout = requestTimeout
        self.spoolPath = spoolPath
        self.spoolCapacity = spoolCapacity
        self.installIdLifetime = installIdLifetime
        self.optOutLifetime = optOutLifetime
        self.logger = logger
        self.onError = onError
    }
}

/// A scope that keeps the app alive while the last flush goes out. ``AnalyticsAppOptions/backgroundScope``
/// is how an app supplies its own; the client falls back to `beginBackgroundTask`.
public protocol AnalyticsBackgroundScope: Sendable {
    func end() async
}

/// Uncommon apps-only options.
public struct AnalyticsAppOptions: Sendable {
    /// Whether the client flushes and spools when the app goes to the background — the last moment
    /// iOS guarantees the process runs.
    public var flushesOnBackground: Bool

    /// A scope that prevents the app from being killed mid-send by the host. Nil uses the client's
    /// own `beginBackgroundTask`.
    public var backgroundScope: (@Sendable (String) async -> (any AnalyticsBackgroundScope)?)?

    public init(
        flushesOnBackground: Bool = true,
        backgroundScope: (@Sendable (String) async -> (any AnalyticsBackgroundScope)?)? = nil
    ) {
        self.flushesOnBackground = flushesOnBackground
        self.backgroundScope = backgroundScope
    }
}

/// A snapshot of what the client did since it was created.
public struct AnalyticsStats: Equatable, Sendable {
    /// Queued by `track`.
    public var accepted: Int = 0
    /// Refused by `track`: opted out, bad install id, excluded country, unusable event name.
    public var rejected: Int = 0
    /// Lost rather than sent: a saturated rate window, a full queue, a full spool, an event older
    /// than the collector accepts, or a permanently refused request. A saturated window and a full
    /// queue are counted here although `track` never accepted them — the loss is what matters.
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
