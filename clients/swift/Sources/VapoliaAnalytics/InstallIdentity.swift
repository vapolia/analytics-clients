import Foundation

/// The installation id, and the two flags that go with it. It lives in `UserDefaults` and
/// deliberately not in the Keychain: a Keychain item survives the app being deleted, which would make
/// the id outlive the installation it names. Reinstalling must start a new one, and `UserDefaults` is
/// what the app's privacy manifest already declares (CA92.1).
final class InstallIdentity: @unchecked Sendable {
    private let defaults: UserDefaults
    private let now: @Sendable () -> Date
    private let lock = NSLock()

    init(defaults: UserDefaults = .standard, now: @escaping @Sendable () -> Date = { Date() }) {
        self.defaults = defaults
        self.now = now
    }

    /// The current id, reissued when the old one reached its ceiling.
    func current() -> String {
        lock.lock()
        defer { lock.unlock() }

        let today = now()
        let issuedAt = defaults.object(forKey: Keys.issuedAt) as? Date

        // A device clock moved backwards would otherwise freeze the id: reissue rather than extend.
        let expired = issuedAt.map {
            today.timeIntervalSince($0) >= Self.maxAge || $0.timeIntervalSince(today) > 86_400
        } ?? true

        if !expired, let id = Clean.installId(defaults.string(forKey: Keys.installId)) {
            return id
        }

        let fresh = UUID().uuidString.lowercased()
        defaults.set(fresh, forKey: Keys.installId)
        defaults.set(today, forKey: Keys.issuedAt)
        return fresh
    }

    /// Whether `first_open` still has to be sent. It is kept apart from the id on purpose: a renewal
    /// is not a new installation, and counting it as one would inflate installs every 13 months.
    var firstOpenPending: Bool {
        !defaults.bool(forKey: Keys.firstOpenSent)
    }

    func markFirstOpenSent() {
        defaults.set(true, forKey: Keys.firstOpenSent)
    }

    var optedOut: Bool {
        get { defaults.bool(forKey: Keys.optedOut) }
        set {
            lock.lock()
            defer { lock.unlock() }

            defaults.set(newValue, forKey: Keys.optedOut)
            // Opting out forgets the id: opting back in later must not resume the same installation.
            if newValue {
                defaults.removeObject(forKey: Keys.installId)
                defaults.removeObject(forKey: Keys.issuedAt)
            }
        }
    }

    /// 13 months is the legal ceiling, with no extension. The margin absorbs clock drift.
    private static let maxAge: TimeInterval = 390 * 24 * 60 * 60

    private enum Keys {
        static let installId = "vapolia.analytics.installId"
        static let issuedAt = "vapolia.analytics.installIdIssuedAt"
        static let firstOpenSent = "vapolia.analytics.firstOpenSent"
        static let optedOut = "vapolia.analytics.optedOut"
    }
}
