import XCTest
@testable import VapoliaAnalytics

final class SpoolTests: XCTestCase {

    private var url: URL!

    override func setUpWithError() throws {
        url = FileManager.default.temporaryDirectory
            .appendingPathComponent("spool-\(UUID().uuidString).json")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: url)
    }

    private func pending(_ name: String, ts: TimeInterval = 1_757_500_000) -> Pending {
        Pending(
            key: BatchKey(
                installId: "11111111-0000-0000-0000-000011111111",
                device: Device(country: "FR"),
                context: ["plan": "premium"]
            ),
            event: Event(
                name: name,
                ts: Date(timeIntervalSince1970: ts),
                props: ["result": "win", "moves": 34, "ok": true]
            )
        )
    }

    func testRoundTripsEventsDeviceContextIncluded() {
        let spool = Spool(url: url, capacity: 10)
        let items = [pending("app_open"), pending("game_end")]

        spool.save(items)

        XCTAssertEqual(spool.load(), items)
    }

    func testReadingConsumesTheFile() {
        let spool = Spool(url: url, capacity: 10)
        spool.save([pending("app_open")])

        XCTAssertEqual(spool.load().count, 1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
        XCTAssertTrue(spool.load().isEmpty)
    }

    func testSavingNothingClearsTheFile() {
        let spool = Spool(url: url, capacity: 10)
        spool.save([pending("app_open")])

        spool.save([])

        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
    }

    func testATruncatedFileYieldsNothing() throws {
        let spool = Spool(url: url, capacity: 10)
        spool.save([pending("app_open"), pending("game_end")])
        let data = try Data(contentsOf: url)
        try data.prefix(data.count / 2).write(to: url)

        XCTAssertTrue(spool.load().isEmpty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
    }

    func testCapacityKeepsTheOldestEvents() {
        let spool = Spool(url: url, capacity: 2)

        spool.save([pending("first"), pending("second"), pending("third")])

        XCTAssertEqual(spool.load().map(\.event.name), ["first", "second"])
    }
}
