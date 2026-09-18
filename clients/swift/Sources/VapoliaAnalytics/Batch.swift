import Foundation

/// A property value. The collector only ever accepts scalars, so this is the whole type — and the
/// literal conformances below are what let a call site stay readable:
///
/// ```swift
/// Analytics.track("game_end", ["result": "win", "moves": 34, "ended": true])
/// ```
public enum PropValue: Hashable, Codable, Sendable {
    case string(String)
    case number(Double)
    case bool(Bool)

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .string(let value):
            try container.encode(value)
        case .bool(let value):
            try container.encode(value)
        case .number(let value):
            // Whole values are encoded as integers, so `moves` reads as 34 and not 34.0.
            if value == value.rounded(), abs(value) < 1e15 {
                try container.encode(Int64(value))
            } else {
                try container.encode(value)
            }
        }
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Double.self) {
            self = .number(value)
        } else {
            self = .string(try container.decode(String.self))
        }
    }
}

extension PropValue: ExpressibleByStringLiteral {
    public init(stringLiteral value: String) { self = .string(value) }
}

extension PropValue: ExpressibleByIntegerLiteral {
    public init(integerLiteral value: Int) { self = .number(Double(value)) }
}

extension PropValue: ExpressibleByFloatLiteral {
    public init(floatLiteral value: Double) { self = .number(value) }
}

extension PropValue: ExpressibleByBooleanLiteral {
    public init(booleanLiteral value: Bool) { self = .bool(value) }
}

struct Event: Hashable, Codable, Sendable {
    let name: String
    let ts: Date
    let props: [String: PropValue]
    /// Minutes east of UTC when the event was tracked, apart from `ts` which is UTC. Nil when unknown, including in a spool written
    /// before it existed.
    var tz: Int? = nil
}

/// What groups events into one request: one installation, one device, one batch context.
struct BatchKey: Hashable, Codable, Sendable {
    let installId: String
    let device: Device
    /// What is true of the installation for this batch. Part of the key: an event carries the state
    /// it was produced under, so two contexts cannot share a request.
    var context: [String: PropValue] = [:]
}

struct Pending: Hashable, Codable, Sendable {
    let key: BatchKey
    let event: Event
}

/// The wire shape of `POST /{source}`.
struct BatchPayload: Encodable {
    let installId: String
    let buildToken: String?
    let build: String?
    let platform: String?
    let osVersion: String?
    let deviceClass: String?
    let locale: String?
    let country: String?
    let store: String?
    let context: [String: PropValue]?
    let events: [EventPayload]

    struct EventPayload: Encodable {
        let name: String
        let ts: String
        let props: [String: PropValue]?
        let tz: Int?
    }

    init(key: BatchKey, events: [Event], timestamps: ISO8601DateFormatter, buildToken: String? = nil) {
        installId = key.installId
        // With a build token the collector takes both from it: sending them too would only be ignored.
        let token = buildToken.flatMap { $0.trimmingCharacters(in: .whitespaces).isEmpty ? nil : $0 }
        self.buildToken = token
        build = token == nil ? key.device.build : nil
        platform = token == nil ? key.device.platform : nil
        osVersion = key.device.osVersion
        deviceClass = key.device.deviceClass
        locale = key.device.locale
        country = key.device.country
        store = key.device.store
        context = key.context.isEmpty ? nil : key.context

        // Sorted by timestamp: events reach the buffer through concurrent tasks, so their arrival
        // order is not guaranteed.
        self.events = events.sorted { $0.ts < $1.ts }.map {
            EventPayload(
                name: $0.name,
                ts: timestamps.string(from: $0.ts),
                props: $0.props.isEmpty ? nil : $0.props,
                tz: $0.tz
            )
        }
    }
}

enum BatchEncoder {
    static func makeTimestampFormatter() -> ISO8601DateFormatter {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        formatter.timeZone = TimeZone(identifier: "UTC")
        return formatter
    }

    static func encode(
        key: BatchKey,
        events: [Event],
        timestamps: ISO8601DateFormatter,
        buildToken: String? = nil
    ) throws -> Data {
        let encoder = JSONEncoder()
        // Sorted keys so a payload is comparable across runs; a nil field is simply not encoded.
        encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(BatchPayload(key: key, events: events, timestamps: timestamps, buildToken: buildToken))
    }
}
