import Foundation

/// What survives the process being killed — the common case on iOS, where a suspended app is ended
/// without notice. The file lives in Caches: unsent counters have no business being backed up to
/// iCloud and restored onto another device, and the system purging them costs a few counts.
///
/// Events are read back once: ``load()`` deletes the file. A duplicated event is a wrong count, a
/// lost one only a missing count, so the ambiguity is resolved towards losing.
struct Spool: Sendable {
    let url: URL
    let capacity: Int

    private struct File: Codable {
        let version: Int
        let items: [Pending]
    }

    private static let version = 2

    static func defaultURL(source: String) -> URL? {
        guard let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
        else { return nil }

        return caches.appendingPathComponent("vapolia-analytics-\(source).json")
    }

    /// Returns how many events did not fit, so the caller can count them as lost. The caller passes
    /// them oldest first; beyond the cap the oldest are kept, being the ones a relaunch is meant to
    /// recover.
    @discardableResult
    func save(_ items: [Pending]) -> Int {
        guard !items.isEmpty else {
            clear()
            return 0
        }

        let kept = items.count > capacity ? Array(items.prefix(capacity)) : items

        do {
            let data = try JSONEncoder().encode(File(version: Self.version, items: kept))
            try data.write(to: url, options: [.atomic])
        } catch {
            // Nothing to do about it, and nothing a caller could do either.
            return items.count
        }

        return items.count - kept.count
    }

    func load() -> [Pending] {
        defer { clear() }

        guard let data = try? Data(contentsOf: url),
              let file = try? JSONDecoder().decode(File.self, from: data),
              file.version == Self.version
        else {
            // A truncated or older file is not worth a migration path.
            return []
        }

        return file.items.count > capacity ? Array(file.items.prefix(capacity)) : file.items
    }

    func clear() {
        try? FileManager.default.removeItem(at: url)
    }
}
