import Foundation

#if canImport(UIKit)
import UIKit
#endif

/// The entry point. One call at launch, one call per event:
///
/// ```swift
/// Analytics.start(source: "<sourceName>")
/// Analytics.track("game_end", ["result": "win", "moves": 34])
/// ```
///
/// It holds the installation id, the device and the sender, and emits no event of its own. Use
/// ``isFirstRun`` and ``onForeground`` to place your own `first_open` and `app_open`.
public final class Analytics: @unchecked Sendable {

    public static let shared = Analytics()

    private let lock = NSLock()
    private var config: AnalyticsConfig?
    private var sender: Sender?
    private var identity: InstallIdentity?
    private var counters = Counters()
    private var device = Device()
    private var batchContext: ( () -> [String: PropValue])?
    private var foregroundHandler: ( () -> Void)?
    private var backgroundHandler: ( () -> Void)?
    private var observers: [NSObjectProtocol] = []
    private var warned = false

    private init() {}

    // MARK: - Lifecycle

    /// Starts the sender. Calling it twice is a no-op: the second call keeps the first configuration.
    /// `endpoint` is the collector's base URL and has no default.
    @MainActor
    public static func start(source: String, endpoint: String) {
        start(AnalyticsConfig(source: source, endpoint: endpoint))
    }

    @MainActor
    public static func start(_ config: AnalyticsConfig) {
        shared.start(config)
    }

    @MainActor
    private func start(_ config: AnalyticsConfig) {
        lock.lock()
        guard sender == nil else {
            lock.unlock()
            return
        }

        guard let url = URL(string: config.endpoint.trimmingTrailingSlash() + "/" + config.source) else {
            lock.unlock()
            config.logger?.error("\(config.endpoint) is not a usable endpoint: nothing will be sent")
            return
        }

        let identity = InstallIdentity()
        let counters = Counters()
        let spool = Spool.defaultURL(source: config.source).map {
            Spool(url: $0, capacity: config.spoolCapacity)
        }
        let sender = Sender(
            config: config,
            poster: URLSessionPoster(url: url, timeout: config.requestTimeout, token: config.token),
            spool: spool,
            counters: counters
        )

        self.config = config
        self.identity = identity
        self.counters = counters
        self.sender = sender
        device = DeviceProbe.detect()
        lock.unlock()

        Task { await sender.start() }
        observeAppLifecycle()
    }

    /// One last flush, then the sender stops. What it cannot send is left on disk for the next launch.
    @MainActor
    public static func stop() async {
        await shared.stop()
    }

    @MainActor
    private func stop() async {
        lock.lock()
        let sender = self.sender
        let observers = self.observers
        self.sender = nil
        self.observers = []
        lock.unlock()

        observers.forEach { NotificationCenter.default.removeObserver($0) }
        await sender?.stop()
    }

    // MARK: - Tracking

    /// Queues one event. Never blocks, never throws. `name` and every property key must be on the
    /// source's whitelist, which this client cannot know.
    public static func track(_ name: String, _ props: [String: PropValue] = [:]) {
        shared.track(name, props)
    }

    private func track(_ name: String, _ props: [String: PropValue]) {
        lock.lock()
        let sender = self.sender
        let identity = self.identity
        let config = self.config
        let counters = self.counters
        let device = self.device
        let alreadyWarned = warned
        warned = true
        lock.unlock()

        guard let sender, let identity, let config else {
            if !alreadyWarned {
                print("[analytics] Analytics.start() was never called: \"\(name)\" and the next ones are ignored")
            }
            return
        }

        guard !identity.optedOut else { return }

        guard counters.withinRate(
            limit: config.maxEventsPerWindow,
            window: config.rateWindow,
            now: Date()
        ) else { return }

        guard let installId = Clean.installId(identity.current()),
              let eventName = Clean.text(name, maxLength: Limits.maxValueLength),
              let cleanDevice = device.cleaned(excluding: config.excludedCountries)
        else {
            counters.rejected()
            return
        }

        guard counters.reserve(limit: config.queueCapacity) else { return }

        let now = Date()
        let pending = Pending(
            key: BatchKey(
                installId: installId,
                device: cleanDevice,
                context: Clean.props(batchContext?() ?? [:], maxKeys: Limits.maxContextKeys)
            ),
            event: Event(
                name: eventName,
                ts: now,
                props: Clean.props(props),
                // Minutes east of UTC at that instant, so a DST change between two events is not smoothed over.
                tz: TimeZone.current.secondsFromGMT(for: now) / 60
            )
        )
        Task { await sender.add(pending) }
    }

    /// Sends what is queued, without waiting for it.
    public static func flush() {
        let sender = shared.currentSender
        Task { await sender?.flush(persist: false) }
    }

    /// Sends what is queued and waits for it.
    public static func flushAndWait() async {
        await shared.currentSender?.flush(persist: false)
    }

    // MARK: - Settings

    /// The right of opposition. Turning it on stops collection, drops what was queued, and forgets the
    /// installation id, so opting back in cannot resume the same installation.
    public static var optedOut: Bool {
        get { shared.currentIdentity?.optedOut ?? false }
        set {
            guard let identity = shared.currentIdentity else { return }
            identity.optedOut = newValue
            if newValue {
                let sender = shared.currentSender
                Task { await sender?.clear() }
            }
        }
    }

    /// The current installation id, for a support screen. Nil when opted out or not started.
    public static var installId: String? {
        guard let identity = shared.currentIdentity, !identity.optedOut else { return nil }
        return identity.current()
    }

    public static var stats: AnalyticsStats {
        shared.lock.lock()
        defer { shared.lock.unlock() }
        return shared.counters.snapshot
    }

    /// Corrects what the device probe reported. Not for anything about the app itself, which belongs
    /// in ``context``.
    public static func updateDevice(_ transform: (Device) -> Device) {
        shared.lock.lock()
        defer { shared.lock.unlock() }
        shared.device = transform(shared.device)
    }

    /// What is true of the installation for a whole batch. Asked for again on every event, so an event
    /// carries the state it was produced under. Every key must be on the source's `context` whitelist.
    public static var context: ( () -> [String: PropValue])? {
        get { shared.currentContext }
        set {
            shared.lock.lock()
            shared.batchContext = newValue
            shared.lock.unlock()
        }
    }

    /// Whether this installation has never been seen before — what decides your own `first_open`.
    /// Kept apart from the id, so a rotation does not count as a new installation.
    public static var isFirstRun: Bool {
        shared.currentIdentity?.firstOpenPending ?? false
    }

    /// Records that the installation has been seen. Call it once you emitted your own `first_open`.
    public static func markSeen() {
        shared.currentIdentity?.markFirstOpenSent()
    }

    /// The app returning to the foreground — where an app names its own `app_open`. It hangs off the
    /// notification observer the client already holds, rather than a second one.
    public static var onForeground: ( () -> Void)? {
        get { shared.locked { shared.foregroundHandler } }
        set { shared.locked { shared.foregroundHandler = newValue } }
    }

    /// The app leaving the foreground, just before the queue is flushed and written down.
    public static var onBackground: ( () -> Void)? {
        get { shared.locked { shared.backgroundHandler } }
        set { shared.locked { shared.backgroundHandler = newValue } }
    }

    // MARK: - Internals

    /// NSLock.withLock needs iOS 16; this package targets 15.
    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    private var currentContext: ( () -> [String: PropValue])? {
        lock.lock()
        defer { lock.unlock() }
        return batchContext
    }

    private var currentSender: Sender? {
        lock.lock()
        defer { lock.unlock() }
        return sender
    }

    private var currentIdentity: InstallIdentity? {
        lock.lock()
        defer { lock.unlock() }
        return identity
    }

    /// Leaving the foreground is the moment to write the queue down: the process may not come back.
    @MainActor
    private func observeAppLifecycle() {
        #if canImport(UIKit)
        let center = NotificationCenter.default

        // willEnterForeground rather than didBecomeActive: the latter also fires after a phone call
        // or a pulled-down notification centre, which are not app opens.
        let foreground = center.addObserver(
            forName: UIApplication.willEnterForegroundNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.locked { self?.foregroundHandler }?()
        }

        let background = center.addObserver(
            forName: UIApplication.didEnterBackgroundNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.locked { self?.backgroundHandler }?()
            self?.flushInBackground()
        }

        lock.lock()
        observers = [foreground, background]
        lock.unlock()
        #endif
    }

    /// A backgrounded app gets a few seconds of network before it is suspended: the difference between
    /// sending the session and spooling it to the next launch.
    @MainActor
    private func flushInBackground() {
        #if canImport(UIKit)
        let sender = currentSender
        var taskId = UIBackgroundTaskIdentifier.invalid
        taskId = UIApplication.shared.beginBackgroundTask(withName: "vapolia.analytics.flush") {
            UIApplication.shared.endBackgroundTask(taskId)
            taskId = .invalid
        }

        Task { @MainActor in
            await sender?.flush(persist: true)
            if taskId != .invalid {
                UIApplication.shared.endBackgroundTask(taskId)
                taskId = .invalid
            }
        }
        #endif
    }
}

private extension String {
    func trimmingTrailingSlash() -> String {
        hasSuffix("/") ? String(dropLast()) : self
    }
}
