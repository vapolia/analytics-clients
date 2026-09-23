import Foundation

#if canImport(UIKit)
import UIKit
#endif

/// The entry point. One call at launch, one call per event:
///
/// ```swift
/// Analytics.start(ingestionUrl: URL(string: "https://analytics.example.com/<sourceName>")!)
/// Analytics.track("game_end", ["result": "win", "moves": 34])
/// ```
///
/// It holds the installation id, the device and the sender, and emits no event of its own. Use
/// ``isFirstRun`` and ``onForeground`` to place your own `first_open` and `app_open`.
public final class Analytics: @unchecked Sendable {

    public static let shared = Analytics()

    private let lock = NSLock()
    private var options: AnalyticsOptions?
    private var sender: Sender?
    private var identity: InstallIdentity?
    private var counters = Counters()
    private var device = Device()
    private var batchContext: (@Sendable () -> [String: PropValue]?)?
    private var foregroundHandler: (@Sendable () -> Void)?
    private var backgroundHandler: (@Sendable () -> Void)?
    private var observers: [NSObjectProtocol] = []
    private var warned = false

    private init() {}

    // MARK: - Lifecycle

    /// Starts the sender. Calling it twice is a no-op: the second call keeps the first configuration.
    /// `ingestionUrl` is `https://baseUrl/sourceName` — the collector's URL for this app.
    @MainActor
    public static func start(ingestionUrl: URL) {
        start(AnalyticsOptions(ingestionUrl: ingestionUrl))
    }

    @MainActor
    public static func start(_ options: AnalyticsOptions) {
        shared.start(options)
    }

    @MainActor
    private func start(_ options: AnalyticsOptions) {
        lock.lock()
        guard sender == nil else {
            lock.unlock()
            return
        }

        guard !options.isDebugBuild else {
            lock.unlock()
            return
        }

        guard options.ingestionUrl.scheme != nil, !options.source.isEmpty else {
            lock.unlock()
            options.advanced.logger?.error(
                "\(options.ingestionUrl) is not a usable ingestion URL — it must be "
                    + "https://baseUrl/sourceName. Nothing will be sent."
            )
            return
        }

        let identity = InstallIdentity(
            idLifetime: options.advanced.installIdLifetime,
            refusalLifetime: options.advanced.optOutLifetime,
            requiresPriorConsent: options.requiresPriorConsent
                ?? localeRequiresPriorConsent(DeviceProbe.currentLocale)
        )
        if let seed = options.seedInstallId?() {
            identity.seed(seed)
        }

        let counters = Counters()
        // Zero capacity is how an app turns the spool off, as in the .NET client.
        let spool = options.advanced.spoolCapacity > 0
            ? (options.advanced.spoolPath ?? Spool.defaultURL(source: options.source))
                .map { Spool(url: $0, capacity: options.advanced.spoolCapacity) }
            : nil
        let sender = Sender(
            options: options,
            poster: URLSessionPoster(
                url: options.ingestionUrl,
                timeout: options.advanced.requestTimeout,
                token: options.token
            ),
            spool: spool,
            counters: counters
        )

        self.options = options
        self.identity = identity
        self.counters = counters
        self.sender = sender
        if let context = options.context {
            batchContext = context
        }
        device = DeviceProbe.detect()
        lock.unlock()

        Task { await sender.start() }
        // What a previous session spooled must not leave while the person is opted out — or has not
        // yet answered, under a regime that asks first.
        if identity.isOptedOut {
            Task { await sender.clear() }
        }
        observeAppLifecycle(options.app)
    }

    /// One last flush, then the sender stops. What it cannot send is left on disk for the next launch.
    @MainActor
    public static func stop() async {
        await shared.stop()
    }

    @MainActor
    private func stop() async {
        // Through `locked`, not `lock.lock()`: NSLock is unavailable from an async context, and
        // taking it around a non-async body is what makes that true here.
        let (sender, observers) = locked { () -> (Sender?, [NSObjectProtocol]) in
            let taken = (self.sender, self.observers)
            self.sender = nil
            self.observers = []
            return taken
        }

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
        let options = self.options
        let counters = self.counters
        let device = self.device
        let alreadyWarned = warned
        warned = true
        lock.unlock()

        guard let sender, let identity, let options else {
            if !alreadyWarned {
                print("[analytics] Analytics.start() was never called: \"\(name)\" and the next ones are ignored")
            }
            return
        }

        guard !identity.isOptedOut else { return }

        guard counters.withinRate(
            limit: options.advanced.maxEventsPerWindow,
            window: options.advanced.rateWindow,
            now: Date()
        ) else {
            report(options, nil, "dropping \"\(name)\": the rate window is saturated", permanent: false)
            return
        }

        guard let installId = Clean.installId(identity.current()),
              let eventName = Clean.text(name, maxLength: Limits.maxValueLength),
              let cleanDevice = device.cleaned(excluding: options.excludedCountries)
        else {
            counters.rejected()
            report(options, nil, "refusing \"\(name)\": unusable name, install id or country", permanent: true)
            return
        }

        guard counters.reserve(limit: options.advanced.queueCapacity) else {
            report(options, nil, "dropping \"\(name)\": the queue is full", permanent: false)
            return
        }

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
        // Not a `Task`: one per event would cost an allocation each and, worse, arrive in no
        // particular order — an event tracked just before a flush could miss it.
        sender.enqueue(pending)
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
    public static var isOptedOut: Bool {
        get { shared.currentIdentity?.isOptedOut ?? false }
        set {
            guard let identity = shared.currentIdentity else { return }
            identity.isOptedOut = newValue
            if newValue {
                let sender = shared.currentSender
                Task { await sender?.clear() }
            }
        }
    }

    /// The current installation id, for a support screen. Nil when opted out or not started.
    public static var installId: String? {
        guard let identity = shared.currentIdentity, !identity.isOptedOut else { return nil }
        return identity.current()
    }

    public static var stats: AnalyticsStats {
        // Only the field read needs our lock; `Counters` has its own, and nesting the two would
        // impose a lock order for nothing.
        shared.locked { shared.counters }.snapshot
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
    public static var context: (@Sendable () -> [String: PropValue]?)? {
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

    /// When this installation was first seen, kept across id renewals. Feed it to ``InstallAge`` if
    /// your source whitelists a bucket for it.
    public static var firstSeen: Date? {
        shared.currentIdentity?.firstSeen
    }

    /// Takes an identity issued elsewhere, for a client migrating off another SDK. Only before the
    /// client ever issued one of its own; returns whether the seed was taken.
    @discardableResult
    public static func seed(_ seed: InstallSeed) -> Bool {
        shared.currentIdentity?.seed(seed) ?? false
    }

    /// The app returning to the foreground — where an app names its own `app_open`. It hangs off the
    /// notification observer the client already holds, rather than a second one.
    public static var onForeground: (@Sendable () -> Void)? {
        get { shared.locked { shared.foregroundHandler } }
        set { shared.locked { shared.foregroundHandler = newValue } }
    }

    /// The app leaving the foreground, just before the queue is flushed and written down.
    public static var onBackground: (@Sendable () -> Void)? {
        get { shared.locked { shared.backgroundHandler } }
        set { shared.locked { shared.backgroundHandler = newValue } }
    }

    // MARK: - Internals

    private func report(_ options: AnalyticsOptions, _ error: Error?, _ reason: String, permanent: Bool) {
        if permanent {
            options.advanced.logger?.error(reason)
        } else {
            options.advanced.logger?.warn(reason)
        }
        options.advanced.onError?(error, reason, permanent)
    }

    /// NSLock.withLock needs iOS 16; this package targets 15.
    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    private var currentContext: (@Sendable () -> [String: PropValue]?)? {
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
    private func observeAppLifecycle(_ app: AnalyticsAppOptions) {
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
            guard let self else { return }
            self.locked { self.backgroundHandler }?()
            guard app.flushesOnBackground else { return }
            // Delivered on the main queue, but the closure is not main-actor isolated and iOS 15
            // predates `MainActor.assumeIsolated`: hop explicitly rather than assert.
            Task { @MainActor in self.flushInBackground(app.backgroundScope) }
        }

        lock.lock()
        observers = [foreground, background]
        lock.unlock()
        #endif
    }

    /// A backgrounded app gets a few seconds of network before it is suspended: the difference
    /// between sending the session and spooling it to the next launch.
    @MainActor
    private func flushInBackground(
        _ scope: (@Sendable (String) async -> (any AnalyticsBackgroundScope)?)?
    ) {
        let sender = currentSender
        let name = "vapolia.analytics.flush"

        // An app that has its own way of holding the process alive passes it in; otherwise the
        // client uses the only one UIKit offers.
        if let scope {
            Task { @MainActor in
                let held = await scope(name)
                await sender?.flush(persist: true)
                await held?.end()
            }
            return
        }

        #if canImport(UIKit)
        var taskId = UIBackgroundTaskIdentifier.invalid
        taskId = UIApplication.shared.beginBackgroundTask(withName: name) {
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
        #else
        Task { await sender?.flush(persist: true) }
        #endif
    }
}
