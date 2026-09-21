import XCTest
@testable import VapoliaAnalytics

/// Opens once, and lets every waiter through.
private actor Gate {
    private var opened = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func open() {
        opened = true
        waiters.forEach { $0.resume() }
        waiters.removeAll()
    }

    func wait() async {
        guard !opened else { return }
        await withCheckedContinuation { waiters.append($0) }
    }
}

/// Counts occurrences, and lets a test wait for the nth one.
private actor Tally {
    private var count = 0
    private var waiters: [(Int, CheckedContinuation<Void, Never>)] = []

    func fire() {
        count += 1
        waiters.removeAll { target, continuation in
            guard count >= target else { return false }
            continuation.resume()
            return true
        }
    }

    func wait(for target: Int) async {
        guard count < target else { return }
        await withCheckedContinuation { waiters.append((target, $0)) }
    }
}

/// A collector that holds every request open until the test lets it answer.
private struct GatedPoster: HTTPPoster {
    let entered: Tally
    let gate: Gate
    let bodies: Bodies
    let answer: SendResult

    actor Bodies {
        private(set) var all: [String] = []
        func append(_ body: String) { all.append(body) }
    }

    func post(_ body: Data) async -> SendResult {
        await bodies.append(String(decoding: body, as: UTF8.self))
        await entered.fire()
        await gate.wait()
        return answer
    }
}

/// What the command queue guarantees, and what the spool owes the counters.
final class OrderingTests: XCTestCase {

    private let installId = "11111111-0000-0000-0000-000011111111"

    private func config(
        batchSize: Int = Limits.maxEventsPerBatch,
        maxAttempts: Int = 1,
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

    /// `age` is seconds before now: an event older than `Limits.maxEventAge` would be dropped
    /// before it ever reached the collector, which is not what these tests are about.
    private func pending(_ name: String, age: TimeInterval, context: [String: PropValue] = [:]) -> Pending {
        Pending(
            key: BatchKey(
                installId: installId,
                device: Device(),
                context: context
            ),
            event: Event(name: name, ts: Date().addingTimeInterval(-age), props: [:])
        )
    }

    private func spoolURL() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("spool-\(UUID().uuidString).json")
    }

    /// The whole point of the command queue: `track` hands the event over without waiting, so a
    /// flush issued right after it must still find the event. An unstructured task per event did
    /// not guarantee that, and an event tracked as the app backgrounds would be lost.
    func testAnEventEnqueuedBeforeAFlushIsInThatFlush() async throws {
        let collector = FakeRecorder()
        let counters = Counters()
        let sender = Sender(options: config(), poster: collector, spool: nil, counters: counters)

        sender.enqueue(pending("app_open", age: 2))
        sender.enqueue(pending("game_end", age: 1))
        await sender.flush(persist: false)

        let bodies = await collector.bodies
        XCTAssertEqual(bodies.count, 1, "both events should have been in the flush")
        let body = try XCTUnwrap(bodies.first)
        XCTAssertTrue(body.contains("app_open"), body)
        XCTAssertTrue(body.contains("game_end"), body)
        XCTAssertEqual(counters.snapshot.sent, 2)
    }

    /// Opting out while a request is in flight must leave nothing behind. `keep` puts unsent events
    /// back, so the clear has to be ordered after it, not merely issued after it.
    func testOptingOutDuringAnInFlightSendLeavesNothingBuffered() async {
        let gate = Gate()
        let poster = GatedPoster(
            entered: Tally(),
            gate: gate,
            bodies: GatedPoster.Bodies(),
            // Kept, not dropped: this is exactly the case where `keep` re-buffers.
            answer: .retry(after: 0, reason: "offline")
        )
        let counters = Counters()
        let sender = Sender(options: config(), poster: poster, spool: nil, counters: counters)

        await sender.add(pending("app_open", age: 1))
        let flushing = Task { await sender.flush(persist: false) }
        await poster.entered.wait(for: 1)

        // Issued while the send is parked inside `post`.
        let clearing = Task { await sender.clear() }
        await gate.open()
        await flushing.value
        await clearing.value

        // A second flush proves the buffers are empty: nothing new reaches the collector.
        await sender.flush(persist: false)
        let bodies = await poster.bodies.all
        XCTAssertEqual(bodies.count, 1, "the opt-out should have dropped the re-buffered event")
    }

    /// `stop` persists what is left. A flush still in flight must not put its events back *after*
    /// that write, which would leave them in memory alone and lose them with the process.
    func testStopIssuedDuringAFlushStillSpoolsEverything() async throws {
        let url = spoolURL()
        defer { try? FileManager.default.removeItem(at: url) }

        let gate = Gate()
        let poster = GatedPoster(
            entered: Tally(),
            gate: gate,
            bodies: GatedPoster.Bodies(),
            answer: .retry(after: 0, reason: "offline")
        )
        let sender = Sender(
            options: config(),
            poster: poster,
            spool: Spool(url: url, capacity: 100),
            counters: Counters()
        )

        await sender.add(pending("app_open", age: 1))
        let flushing = Task { await sender.flush(persist: false) }
        await poster.entered.wait(for: 1)

        let stopping = Task { await sender.stop() }
        await gate.open()
        await flushing.value
        await stopping.value

        let spooled = Spool(url: url, capacity: 100).load()
        XCTAssertEqual(spooled.map(\.event.name), ["app_open"], "the unsent event should be on disk")
    }

    /// `buffers` is a Dictionary, so its iteration order is the hash order. Without an explicit sort
    /// the spool's cap would keep an arbitrary subset instead of the oldest events.
    func testPersistKeepsTheOldestEventsAcrossGroups() async {
        let url = spoolURL()
        defer { try? FileManager.default.removeItem(at: url) }

        let collector = FakeRecorder(answers: [
            .retry(after: 0, reason: "offline"),
            .retry(after: 0, reason: "offline"),
            .retry(after: 0, reason: "offline"),
            .retry(after: 0, reason: "offline"),
        ])
        let sender = Sender(
            options: config(),
            poster: collector,
            spool: Spool(url: url, capacity: 2),
            counters: Counters()
        )

        // Four groups, because the batch context is part of the key. The newest two must be the
        // ones dropped, whatever order the dictionary hands them back in.
        await sender.add(pending("oldest", age: 40, context: ["k": "a"]))
        await sender.add(pending("second", age: 30, context: ["k": "b"]))
        await sender.add(pending("third", age: 20, context: ["k": "c"]))
        await sender.add(pending("newest", age: 10, context: ["k": "d"]))
        await sender.flush(persist: true)

        let spooled = Spool(url: url, capacity: 2).load()
        XCTAssertEqual(spooled.map(\.event.name), ["oldest", "second"])
    }

    /// The doc for `stats.dropped` claims a full spool is counted there. Nothing counted it.
    func testWhatDoesNotFitInTheSpoolIsCountedAsDropped() async {
        let url = spoolURL()
        defer { try? FileManager.default.removeItem(at: url) }

        let collector = FakeRecorder(answers: [
            .retry(after: 0, reason: "offline"),
            .retry(after: 0, reason: "offline"),
            .retry(after: 0, reason: "offline"),
        ])
        let counters = Counters()
        let sender = Sender(
            options: config(),
            poster: collector,
            spool: Spool(url: url, capacity: 1),
            counters: counters
        )

        await sender.add(pending("a", age: 30, context: ["k": "a"]))
        await sender.add(pending("b", age: 20, context: ["k": "b"]))
        await sender.add(pending("c", age: 10, context: ["k": "c"]))
        await sender.flush(persist: true)

        XCTAssertEqual(counters.snapshot.dropped, 2, "two events did not fit in the spool")
    }
}

/// The plain recorder, shared by these tests.
private actor FakeRecorder: HTTPPoster {
    private(set) var bodies: [String] = []
    private var answers: [SendResult]

    init(answers: [SendResult] = []) {
        self.answers = answers
    }

    func post(_ body: Data) async -> SendResult {
        bodies.append(String(decoding: body, as: UTF8.self))
        return answers.isEmpty ? .ok : answers.removeFirst()
    }
}
