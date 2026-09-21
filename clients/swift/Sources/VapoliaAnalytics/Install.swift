import Foundation

/// An installation identity that comes from somewhere else — a client being migrated off another
/// SDK, which already has an id and a first-seen date worth keeping.
public struct InstallSeed: Hashable, Sendable {
    public var installId: String
    public var issuedAt: Date
    public var firstSeen: Date

    public init(installId: String, issuedAt: Date, firstSeen: Date) {
        self.installId = installId
        self.issuedAt = issuedAt
        self.firstSeen = firstSeen
    }
}

/// The age of an installation, in buckets, for an app that wants to segment on it.
///
/// The client keeps the first-seen date — it is the only thing that knows it, and it keeps it across
/// id renewals — but it does not send a bucket on its own: that would be the client naming what is
/// measured. Call this from your ``Analytics/context`` if the key is on your whitelist.
public enum InstallAge {
    /// `"0"`, `"1-7"`, `"8-30"`, `"31-90"` or `"90+"`.
    public static func bucket(firstSeen: Date, now: Date = Date()) -> String {
        switch now.timeIntervalSince(firstSeen) / 86_400 {
        case ..<1: return "0"
        case ..<8: return "1-7"
        case ..<31: return "8-30"
        case ..<91: return "31-90"
        default: return "90+"
        }
    }
}
