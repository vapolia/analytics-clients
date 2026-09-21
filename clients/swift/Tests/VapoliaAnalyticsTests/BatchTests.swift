import XCTest
@testable import VapoliaAnalytics

final class BatchTests: XCTestCase {

    private let installId = "11111111-0000-0000-0000-000011111111"
    private let timestamps = BatchEncoder.makeTimestampFormatter()

    private func encode(
        device: Device,
        context: [String: PropValue] = [:],
        events: [Event]
    ) throws -> String {
        let data = try BatchEncoder.encode(
            key: BatchKey(installId: installId, device: device, context: context),
            events: events,
            timestamps: timestamps
        )
        return String(decoding: data, as: UTF8.self)
    }

    func testEncodesTheBatchShapeTheCollectorExpects() throws {
        let json = try encode(
            device: Device(country: "FR"),
            context: ["plan": "free"],
            events: [
                Event(name: "app_open", ts: Date(timeIntervalSince1970: 1_757_500_000), props: [:]),
                Event(
                    name: "game_end",
                    ts: Date(timeIntervalSince1970: 1_757_500_001),
                    props: ["result": "win", "moves": 34]
                ),
            ]
        )

        XCTAssertTrue(json.contains("\"installId\":\"\(installId)\""), json)
        XCTAssertTrue(json.contains("\"country\":\"FR\""), json)
        XCTAssertTrue(json.contains("\"plan\":\"free\""), json)
        XCTAssertTrue(json.contains("\"moves\":34"), json)
        XCTAssertTrue(json.contains("\"result\":\"win\""), json)
    }

    /// The contract's closed list. This is the test that keeps the four clients from drifting apart
    /// again: the platform, the build, the OS version, the device class, the store and the locale
    /// come from the `Authorization` token, never the body.
    func testTheBodyCarriesTheContractsFieldsAndNothingElse() throws {
        let data = try BatchEncoder.encode(
            key: BatchKey(installId: installId, device: Device(country: "FR"), context: ["plan": "free"]),
            events: [Event(name: "app_open", ts: Date(timeIntervalSince1970: 0), props: ["a": 1])],
            timestamps: timestamps
        )

        let body = try XCTUnwrap(
            JSONSerialization.jsonObject(with: data) as? [String: Any]
        )
        XCTAssertEqual(Set(body.keys), ["installId", "country", "context", "events"])

        let events = try XCTUnwrap(body["events"] as? [[String: Any]])
        XCTAssertEqual(Set(events[0].keys), ["name", "ts", "props"], "plus `tz` when it is known")
    }

    /// A field the device did not fill in is absent, not null.
    func testAnUnknownCountryIsNotSentAtAll() throws {
        let json = try encode(
            device: Device(),
            events: [Event(name: "app_open", ts: Date(timeIntervalSince1970: 0), props: [:])]
        )

        XCTAssertFalse(json.contains("country"), json)
    }

    func testTimestampsAreIso8601InUtc() throws {
        let json = try encode(
            device: Device(),
            events: [Event(name: "app_open", ts: Date(timeIntervalSince1970: 0), props: [:])]
        )

        XCTAssertTrue(json.contains("\"ts\":\"1970-01-01T00:00:00.000Z\""), json)
    }

    func testEventsAreOrderedByTimestamp() throws {
        let json = try encode(
            device: Device(),
            events: [
                Event(name: "second", ts: Date(timeIntervalSince1970: 200), props: [:]),
                Event(name: "first", ts: Date(timeIntervalSince1970: 100), props: [:]),
            ]
        )

        let firstIndex = try XCTUnwrap(json.range(of: "first")).lowerBound
        let secondIndex = try XCTUnwrap(json.range(of: "second")).lowerBound
        XCTAssertLessThan(firstIndex, secondIndex, json)
    }

    func testWholeNumbersKeepNoFractionalPart() throws {
        let json = try encode(
            device: Device(),
            events: [
                Event(
                    name: "game_end",
                    ts: Date(timeIntervalSince1970: 0),
                    props: ["moves": 34, "ratio": 0.5]
                )
            ]
        )

        XCTAssertTrue(json.contains("\"moves\":34"), json)
        XCTAssertTrue(json.contains("\"ratio\":0.5"), json)
    }

    func testEmptyPropsAreOmitted() throws {
        let json = try encode(
            device: Device(),
            events: [Event(name: "app_open", ts: Date(timeIntervalSince1970: 0), props: [:])]
        )

        XCTAssertFalse(json.contains("props"), json)
    }
}
