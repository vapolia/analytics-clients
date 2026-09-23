import Foundation

/// The installation id, and the two flags that go with it. It lives in `UserDefaults` and
/// deliberately not in the Keychain: a Keychain item survives the app being deleted, which would make
/// the id outlive the installation it names. Reinstalling must start a new one, and `UserDefaults` is
/// what the app's privacy manifest already declares (CA92.1).
final class InstallIdentity: @unchecked Sendable {
    private let defaults: UserDefaults
    private let now: @Sendable () -> Date
    private let idLifetime: TimeInterval
    private let refusalLifetime: TimeInterval
    private let requiresPriorConsent: Bool
    private let lock = NSLock()

    init(
        defaults: UserDefaults = .standard,
        now: @escaping @Sendable () -> Date = { Date() },
        idLifetime: TimeInterval = InstallIdentity.defaultLifetime,
        refusalLifetime: TimeInterval = InstallIdentity.defaultLifetime,
        requiresPriorConsent: Bool = false
    ) {
        self.defaults = defaults
        self.now = now
        self.idLifetime = idLifetime
        self.refusalLifetime = refusalLifetime
        self.requiresPriorConsent = requiresPriorConsent
    }

    /// Whether the person has answered the measurement question, either way.
    var answered: Bool {
        defaults.object(forKey: Keys.optedOut) != nil
    }

    /// Takes an identity issued elsewhere, unless this installation already has one. Returns whether
    /// the seed was taken, so a caller can tell a migration from a no-op.
    @discardableResult
    func seed(_ seed: InstallSeed) -> Bool {
        lock.lock()
        defer { lock.unlock() }

        guard defaults.string(forKey: Keys.installId) == nil,
              let id = Clean.installId(seed.installId)
        else { return false }

        defaults.set(id, forKey: Keys.installId)
        defaults.set(seed.issuedAt, forKey: Keys.issuedAt)
        defaults.set(seed.firstSeen, forKey: Keys.firstSeen)
        return true
    }

    /// The current id, reissued when the old one reached its ceiling.
    func current() -> String {
        lock.lock()
        defer { lock.unlock() }

        let today = now()
        let issuedAt = defaults.object(forKey: Keys.issuedAt) as? Date

        // The date this installation was first seen, kept across id renewals: a renewal is not a new
        // installation, and counting it as one would inflate installs every 13 months.
        if defaults.object(forKey: Keys.firstSeen) == nil {
            defaults.set(today, forKey: Keys.firstSeen)
        }

        // A device clock moved backwards would otherwise freeze the id: reissue rather than extend.
        let expired = issuedAt.map {
            today.timeIntervalSince($0) >= idLifetime || $0.timeIntervalSince(today) > 86_400
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

    /// When the installation was first seen, or nil before the first id was issued.
    var firstSeen: Date? {
        defaults.object(forKey: Keys.firstSeen) as? Date
    }

    /// A refusal is remembered for ``refusalLifetime``, and — unlike the identifier — refreshed on
    /// every read: an opposition must not quietly lapse while the app is still in use.
    ///
    /// Before any answer it reads `requiresPriorConsent`. Nothing is written then: an unanswered
    /// question is not a refusal, and the id is minted by ``current()``, which an opted-out client
    /// never calls.
    var isOptedOut: Bool {
        get {
            lock.lock()
            defer { lock.unlock() }

            guard defaults.object(forKey: Keys.optedOut) != nil else { return requiresPriorConsent }
            guard defaults.bool(forKey: Keys.optedOut) else { return false }

            let recordedAt = defaults.object(forKey: Keys.optedOutAt) as? Date ?? now()
            guard now().timeIntervalSince(recordedAt) < refusalLifetime else {
                defaults.removeObject(forKey: Keys.optedOut)
                defaults.removeObject(forKey: Keys.optedOutAt)
                return false
            }

            defaults.set(now(), forKey: Keys.optedOutAt)
            return true
        }
        set {
            lock.lock()
            defer { lock.unlock() }

            defaults.set(newValue, forKey: Keys.optedOut)
            defaults.set(newValue ? now() : nil, forKey: Keys.optedOutAt)
            // Opting out forgets the id: opting back in later must not resume the same installation.
            if newValue {
                defaults.removeObject(forKey: Keys.installId)
                defaults.removeObject(forKey: Keys.issuedAt)
            }
        }
    }

    /// 13 months is the legal ceiling, with no extension. The margin absorbs clock drift.
    static let defaultLifetime: TimeInterval = 390 * 24 * 60 * 60

    /// The same keys as the .NET client, so the two agree on what a stored installation looks like.
    private enum Keys {
        static let installId = "vapolia.analytics.installId"
        static let issuedAt = "vapolia.analytics.installIdIssuedAt"
        static let firstSeen = "vapolia.analytics.firstSeen"
        static let firstOpenSent = "vapolia.analytics.firstOpenSent"
        static let optedOut = "vapolia.analytics.optedOut"
        static let optedOutAt = "vapolia.analytics.optedOutAt"
    }
}
