import XCTest
@testable import VapoliaAnalytics

/// The ceiling lives in `Counters`, counted before anything reaches the sender, so it is tested here
/// on its own — no collector, no sender, no clock to move but the one passed in.
final class RateLimitTests: XCTestCase {

    func testDropsEverythingPastTheCeilingUntilTheWindowEnds() {
        let counters = Counters()
        let start = Date(timeIntervalSince1970: 1_757_500_000)

        for _ in 0..<3 {
            XCTAssertTrue(counters.withinRate(limit: 3, window: 60, now: start))
        }

        XCTAssertFalse(counters.withinRate(limit: 3, window: 60, now: start))
        XCTAssertFalse(counters.withinRate(limit: 3, window: 60, now: start.addingTimeInterval(59)))
        XCTAssertEqual(counters.snapshot.dropped, 2)

        // The window has ended: the count starts again.
        XCTAssertTrue(counters.withinRate(limit: 3, window: 60, now: start.addingTimeInterval(61)))
    }

    func testZeroDisablesTheCeiling() {
        let counters = Counters()
        let now = Date()

        for _ in 0..<200 {
            XCTAssertTrue(counters.withinRate(limit: 0, window: 60, now: now))
        }

        XCTAssertEqual(counters.snapshot.dropped, 0)
    }

    func testARefusalIsCountedAsDroppedNotRejected() {
        let counters = Counters()
        let now = Date()

        XCTAssertTrue(counters.withinRate(limit: 1, window: 60, now: now))
        XCTAssertFalse(counters.withinRate(limit: 1, window: 60, now: now))

        // Dropped: it was emitted and lost. Rejected means the event itself was unusable.
        XCTAssertEqual(counters.snapshot.dropped, 1)
        XCTAssertEqual(counters.snapshot.rejected, 0)
    }

    func testTheWindowIsFixedNotSliding() {
        let counters = Counters()
        let start = Date(timeIntervalSince1970: 1_757_500_000)

        XCTAssertTrue(counters.withinRate(limit: 2, window: 60, now: start))
        // 30s later the window is the same one: the second slot is still the second slot.
        XCTAssertTrue(counters.withinRate(limit: 2, window: 60, now: start.addingTimeInterval(30)))
        XCTAssertFalse(counters.withinRate(limit: 2, window: 60, now: start.addingTimeInterval(59)))
    }
}
