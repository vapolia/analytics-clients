import Foundation

// Limits mirrored from the collector's EventSanitizer, which applies them again on arrival.
// Public: maxEventsPerBatch is used as a default argument value in AnalyticsConfig's public init.
public enum Limits {
    public static let maxEventsPerBatch = 100
    static let maxPropsPerEvent = 12
    /// Batch-context keys kept, mirroring the collector's own ceiling.
    static let maxContextKeys = 12
    static let maxValueLength = 64
    static let maxBuildLength = 24
    static let maxOsVersionLength = 24
    static let maxLocaleLength = 12

    /// How far back the collector accepts a timestamp. Older events are dropped instead of sent.
    static let maxEventAge: TimeInterval = 7 * 24 * 60 * 60
}

enum Clean {
    static let platforms: Set<String> = ["android", "ios", "maccatalyst", "windows", "web"]
    static let deviceClasses: Set<String> = ["phone", "tablet", "desktop", "other"]
    static let stores: Set<String> = ["google", "apple", "other"]

    /// Trims, caps the length, strips control characters, and turns blank into nil. The control pass
    /// is not cosmetic: Postgres rejects U+0000 in `text` and `jsonb`.
    static func text(_ value: String?, maxLength: Int) -> String? {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty
        else { return nil }

        let capped = trimmed.count > maxLength ? String(trimmed.prefix(maxLength)) : trimmed
        let scalars = capped.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }
        let cleaned = String(String.UnicodeScalarView(scalars))
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return cleaned.isEmpty ? nil : cleaned
    }

    /// Exactly two ASCII letters, or nothing: truncating would turn "GERMANY" into "GE", which is
    /// Georgia.
    static func country(_ value: String?) -> String? {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines),
              trimmed.count == 2,
              trimmed.allSatisfy({ $0.isASCII && $0.isLetter })
        else { return nil }

        return trimmed.uppercased()
    }

    static func pick(_ value: String?, from allowed: Set<String>) -> String? {
        guard let normalized = value?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased(),
              allowed.contains(normalized)
        else { return nil }

        return normalized
    }

    /// The canonical UUID form only, and never the nil UUID.
    static func installId(_ value: String?) -> String? {
        guard let id = value?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased(),
              id.count == 36
        else { return nil }

        var allZero = true
        for (index, character) in id.enumerated() {
            switch index {
            case 8, 13, 18, 23:
                if character != "-" { return nil }
            default:
                guard character.isHexDigit else { return nil }
                if character != "0" { allZero = false }
            }
        }

        return allZero ? nil : id
    }

    /// Caps the count and the length of what a call site passed. ``PropValue`` already keeps the
    /// values to the scalars the collector accepts, so there is nothing to reject by type here.
    static func props(
        _ props: [String: PropValue],
        maxKeys: Int = Limits.maxPropsPerEvent
    ) -> [String: PropValue] {
        if props.isEmpty { return [:] }

        var kept: [String: PropValue] = [:]
        // Sorted so what survives the cap does not depend on the dictionary's hash order, and so
        // identical contexts produce the same key.
        for key in props.keys.sorted() {
            if kept.count == maxKeys { break }

            switch props[key] {
            case .string(let value):
                if let cleaned = text(value, maxLength: Limits.maxValueLength) {
                    kept[key] = .string(cleaned)
                }
            case .number(let value):
                // NaN and the infinities are not representable in jsonb.
                if value.isFinite { kept[key] = .number(value) }
            case .bool(let value):
                kept[key] = .bool(value)
            case nil:
                break
            }
        }

        return kept
    }
}
