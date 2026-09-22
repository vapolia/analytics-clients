import XCTest
@testable import VapoliaAnalytics

/// The identity rules the .NET client defines: a bounded id, a refusal that is refreshed rather than
/// bounded, a first-seen date that survives renewals, and a seed for a migrating client.
final class IdentityTests: XCTestCase {

    private var defaults: UserDefaults!
    private var suiteName: String!

    override func setUpWithError() throws {
        suiteName = "vapolia.analytics.tests.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
    }

    override func tearDownWithError() throws {
        defaults.removePersistentDomain(forName: suiteName)
    }

    private func identity(
        now: @escaping @Sendable () -> Date,
        idLifetime: TimeInterval = InstallIdentity.defaultLifetime,
        refusalLifetime: TimeInterval = InstallIdentity.defaultLifetime,
        defaultOptedOut: Bool = false
    ) -> InstallIdentity {
        InstallIdentity(
            defaults: defaults,
            now: now,
            idLifetime: idLifetime,
            refusalLifetime: refusalLifetime,
            defaultOptedOut: defaultOptedOut
        )
    }

    /// A country that asks first: nothing is collected, and nothing is written, until the popup is
    /// answered.
    func testAnUnansweredConsentRegimeOptsOutAndWritesNothing() {
        let clock = Clock(Date(timeIntervalSince1970: 1_700_000_000))
        let identity = identity(now: clock.read, defaultOptedOut: true)

        XCTAssertTrue(identity.optedOut)
        XCTAssertFalse(identity.answered)
        XCTAssertNil(defaults.string(forKey: "vapolia.analytics.installId"))
    }

    /// An acceptance outranks the default, and survives the next launch.
    func testAcceptanceOutranksTheDefault() {
        let clock = Clock(Date(timeIntervalSince1970: 1_700_000_000))
        let identity = identity(now: clock.read, defaultOptedOut: true)
        identity.optedOut = false

        XCTAssertFalse(identity.optedOut)
        let id = identity.current()

        let next = self.identity(now: clock.read, defaultOptedOut: true)
        XCTAssertFalse(next.optedOut)
        XCTAssertEqual(next.current(), id)
    }

    func testTheIdIsReissuedOnceItsLifetimeIsSpent() {
        let start = Date(timeIntervalSince1970: 1_700_000_000)
        let clock = Clock(start)

        let identity = identity(now: clock.read, idLifetime: 100)
        let first = identity.current()

        clock.advance(99)
        XCTAssertEqual(identity.current(), first, "still inside its lifetime")

        clock.advance(2)
        XCTAssertNotEqual(identity.current(), first, "past its lifetime it must be reissued")
    }

    /// A renewal is not a new installation: the date the installation was first seen has to outlive
    /// the id, or an app would count an install again every 13 months.
    func testFirstSeenSurvivesAnIdRenewal() {
        let start = Date(timeIntervalSince1970: 1_700_000_000)
        let clock = Clock(start)

        let identity = identity(now: clock.read, idLifetime: 100)
        let first = identity.current()
        XCTAssertEqual(identity.firstSeen, start)

        clock.advance(200)
        XCTAssertNotEqual(identity.current(), first)
        XCTAssertEqual(identity.firstSeen, start, "first seen must not move with the id")
    }

    /// Unlike the identifier, a refusal is refreshed on every read: an opposition must not lapse
    /// while the app is still being used.
    func testARefusalIsRefreshedOnEveryReadAndLapsesOnlyWhenUnused() {
        let start = Date(timeIntervalSince1970: 1_700_000_000)
        let clock = Clock(start)
        let identity = identity(now: clock.read, refusalLifetime: 100)

        identity.optedOut = true

        clock.advance(90)
        XCTAssertTrue(identity.optedOut, "still within the window, and this read refreshes it")

        clock.advance(90)
        XCTAssertTrue(identity.optedOut, "the previous read pushed the window forward")

        clock.advance(101)
        XCTAssertFalse(identity.optedOut, "unused for longer than its lifetime, it lapses")
    }

    func testOptingOutForgetsTheId() {
        let identity = identity(now: { Date(timeIntervalSince1970: 1_700_000_000) })
        let first = identity.current()

        identity.optedOut = true
        identity.optedOut = false

        XCTAssertNotEqual(identity.current(), first, "opting back in cannot resume the same install")
    }

    func testASeedIsTakenOnceAndOnlyBeforeAnIdOfItsOwn() {
        let seeded = "33333333-0000-0000-0000-000033333333"
        let firstSeen = Date(timeIntervalSince1970: 1_600_000_000)
        let identity = identity(now: { Date(timeIntervalSince1970: 1_700_000_000) })

        XCTAssertTrue(
            identity.seed(
                InstallSeed(
                    installId: seeded,
                    issuedAt: Date(timeIntervalSince1970: 1_699_999_000),
                    firstSeen: firstSeen
                )
            )
        )
        XCTAssertEqual(identity.current(), seeded)
        XCTAssertEqual(identity.firstSeen, firstSeen, "the migrated install keeps its own age")

        XCTAssertFalse(
            identity.seed(
                InstallSeed(installId: "44444444-0000-0000-0000-000044444444", issuedAt: Date(), firstSeen: Date())
            ),
            "an installation that already has an id keeps it"
        )
    }

    func testASeedThatIsNotAUsableIdIsRefused() {
        let identity = identity(now: { Date(timeIntervalSince1970: 1_700_000_000) })

        XCTAssertFalse(identity.seed(InstallSeed(installId: "not-a-uuid", issuedAt: Date(), firstSeen: Date())))
        XCTAssertFalse(
            identity.seed(
                InstallSeed(installId: "00000000-0000-0000-0000-000000000000", issuedAt: Date(), firstSeen: Date())
            ),
            "the nil UUID names no installation"
        )
    }

    func testInstallAgeBuckets() {
        let firstSeen = Date(timeIntervalSince1970: 1_700_000_000)
        func bucket(afterDays days: Double) -> String {
            InstallAge.bucket(firstSeen: firstSeen, now: firstSeen.addingTimeInterval(days * 86_400))
        }

        XCTAssertEqual(bucket(afterDays: 0), "0")
        XCTAssertEqual(bucket(afterDays: 0.9), "0")
        XCTAssertEqual(bucket(afterDays: 1), "1-7")
        XCTAssertEqual(bucket(afterDays: 7.9), "1-7")
        XCTAssertEqual(bucket(afterDays: 8), "8-30")
        XCTAssertEqual(bucket(afterDays: 30.9), "8-30")
        XCTAssertEqual(bucket(afterDays: 31), "31-90")
        XCTAssertEqual(bucket(afterDays: 90.9), "31-90")
        XCTAssertEqual(bucket(afterDays: 91), "90+")
    }
}

/// A clock a test can move by hand.
private final class Clock: @unchecked Sendable {
    private let lock = NSLock()
    private var now: Date

    init(_ start: Date) { now = start }

    var read: @Sendable () -> Date {
        { [self] in
            lock.lock()
            defer { lock.unlock() }
            return now
        }
    }

    func advance(_ seconds: TimeInterval) {
        lock.lock()
        defer { lock.unlock() }
        now = now.addingTimeInterval(seconds)
    }
}
