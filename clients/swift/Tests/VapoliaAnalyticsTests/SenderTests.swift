import XCTest
@testable import VapoliaAnalytics

/// The collector, reduced to what a client can observe: the bodies it received, and what it answered.
private actor FakeCollector: HTTPPoster {
    private(set) var bodies: [String] = []
    private var answers: [SendResult]

    init(answers: [SendResult] = []) {
        self.answers = answers
    }

    func post(_ body: Data) async -> SendResult {
        bodies.append(String(decoding: body, as: UTF8.self))
        return answers.isEmpty ? .ok : answers.removeFirst()
    }

    var count: Int { bodies.count }
}

final class SenderTests: XCTestCase {

    private let installId = "11111111-0000-0000-0000-000011111111"
    private let otherInstallId = "22222222-0000-0000-0000-000022222222"

    private func config(
        batchSize: Int = Limits.maxEventsPerBatch,
        maxAttempts: Int = 3,
        queueCapacity: Int = 2_000
    ) -> AnalyticsOptions {
        AnalyticsOptions(
            ingestionUrl: URL(string: "https://collector.invalid/testsource")!,
            advanced: .init(
                // Long enough that every test flushes explicitly.
                flushInterval: 3_600,
                batchSize: batchSize,
                queueCapacity: queueCapacity,
                maxAttempts: maxAttempts
            )
        )
    }

    private func pending(
        _ name: String,
        installId: String? = nil,
        device: Device = Device(),
        ts: Date = Date(),
        props: [String: PropValue] = [:]
    ) -> Pending {
        Pending(
            key: BatchKey(installId: installId ?? self.installId, device: device),
            event: Event(name: name, ts: ts, props: props)
        )
    }

    func testSendsOneBatchCarryingTheGlobalProperties() async {
        let collector = FakeCollector()
        let counters = Counters()
        let sender = Sender(options: config(), poster: collector, spool: nil, counters: counters)

        let device = Device(country: "FR")
        await sender.add(pending("app_open", device: device))
        await sender.add(pending("game_end", device: device, props: ["result": "win", "moves": 34]))
        await sender.flush(persist: false)

        let bodies = await collector.bodies
        XCTAssertEqual(bodies.count, 1)
        let body = bodies[0]
        XCTAssertTrue(body.contains("\"installId\":\"\(installId)\""), body)
        XCTAssertTrue(body.contains("\"country\":\"FR\""), body)
        XCTAssertTrue(body.contains("\"moves\":34"), body)
        XCTAssertEqual(counters.snapshot.sent, 2)
    }

    func testGroupsByInstallIdAndDevice() async {
        let collector = FakeCollector()
        let sender = Sender(options: config(), poster: collector, spool: nil, counters: Counters())

        await sender.add(pending("app_open"))
        await sender.add(pending("game_start"))
        await sender.add(pending("app_open", device: Device(country: "DE")))
        await sender.add(pending("app_open", installId: otherInstallId))
        await sender.flush(persist: false)

        let count = await collector.count
        XCTAssertEqual(count, 3)
    }

    func testSendsAsSoonAsABatchIsFull() async {
        let collector = FakeCollector()
        let sender = Sender(options: config(batchSize: 5), poster: collector, spool: nil, counters: Counters())

        for _ in 0..<5 {
            await sender.add(pending("app_open"))
        }

        let count = await collector.count
        XCTAssertEqual(count, 1, "a full batch leaves without waiting for a flush")
    }

    func testKeepsEventsATransientFailureLeftUnsent() async {
        let collector = FakeCollector(answers: [
            .retry(after: 0, reason: "500"),
            .retry(after: 0, reason: "500"),
        ])
        let counters = Counters()
        let sender = Sender(
            options: config(maxAttempts: 2),
            poster: collector,
            spool: nil,
            counters: counters
        )

        await sender.add(pending("app_open"))
        await sender.flush(persist: false)

        XCTAssertEqual(counters.snapshot.requests, 2)
        XCTAssertEqual(counters.snapshot.sent, 0)
        XCTAssertEqual(counters.snapshot.dropped, 0, "a phone that is offline keeps its events")

        // The next flush finds the collector back and sends the same event.
        await sender.flush(persist: false)
        XCTAssertEqual(counters.snapshot.sent, 1)
    }

    func testDropsWhatARetryCannotFix() async {
        let collector = FakeCollector(answers: [.permanent(reason: "unknown source (404)")])
        let counters = Counters()
        let sender = Sender(options: config(), poster: collector, spool: nil, counters: counters)

        await sender.add(pending("app_open"))
        await sender.flush(persist: false)

        let count = await collector.count
        XCTAssertEqual(count, 1, "a permanent refusal is not retried")
        XCTAssertEqual(counters.snapshot.dropped, 1)
    }

    func testDropsEventsOlderThanTheCollectorAccepts() async {
        let collector = FakeCollector()
        let counters = Counters()
        let sender = Sender(options: config(), poster: collector, spool: nil, counters: counters)

        let stale = Date().addingTimeInterval(-Limits.maxEventAge - 60)
        await sender.add(pending("app_open", ts: stale))
        await sender.flush(persist: false)

        let count = await collector.count
        XCTAssertEqual(count, 0)
        XCTAssertEqual(counters.snapshot.dropped, 1)
    }

    func testWhatCouldNotBeSentIsSpooledAndSentOnTheNextLaunch() async throws {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("spool-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: url) }
        let spool = Spool(url: url, capacity: 100)

        let failing = FakeCollector(answers: [.retry(after: 0, reason: "offline")])
        let first = Sender(
            options: config(maxAttempts: 1),
            poster: failing,
            spool: spool,
            counters: Counters()
        )
        await first.add(pending("game_end", props: ["result": "win"]))
        await first.stop()

        XCTAssertTrue(
            FileManager.default.fileExists(atPath: url.path),
            "the queue should have been written down"
        )

        let collector = FakeCollector()
        let restarted = Sender(options: config(), poster: collector, spool: spool, counters: Counters())
        await restarted.start()
        await restarted.flush(persist: false)
        await restarted.stop()

        let bodies = await collector.bodies
        let body = try XCTUnwrap(bodies.first)
        XCTAssertTrue(body.contains("\"name\":\"game_end\""), body)
        XCTAssertTrue(body.contains("\"result\":\"win\""), body)
    }

    func testCountersReserveBoundsTheQueue() {
        let counters = Counters()

        for _ in 0..<200 {
            _ = counters.reserve(limit: 1)
        }

        let stats = counters.snapshot
        XCTAssertEqual(stats.accepted + stats.dropped, 200)
        XCTAssertEqual(stats.accepted, 1, "the queue holds one, the rest is dropped, never blocked")
    }
}
