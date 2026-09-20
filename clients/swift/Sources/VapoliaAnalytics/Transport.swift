import Foundation

enum SendResult: Sendable, Equatable {
    case ok

    /// Worth sending again: a network error, a 5xx, or a 429 with its `Retry-After`.
    case retry(after: TimeInterval, reason: String)

    /// A retry cannot fix it: unknown source, malformed payload.
    case permanent(reason: String)
}

/// What the sender needs from the network. A protocol so the send path can be tested without one.
protocol HTTPPoster: Sendable {
    func post(_ body: Data) async -> SendResult
}

/// One request to `POST {endpoint}/{source}`.
struct URLSessionPoster: HTTPPoster {
    let url: URL
    let session: URLSession
    let token: String?

    init(url: URL, timeout: TimeInterval, token: String? = nil) {
        self.url = url
        self.token = token

        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = timeout
        // Measurement is never worth waking the radio on its own, nor holding a launch.
        configuration.waitsForConnectivity = false
        configuration.urlCache = nil
        session = URLSession(configuration: configuration)
    }

    func post(_ body: Data) async -> SendResult {
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let token, !token.trimmingCharacters(in: .whitespaces).isEmpty {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        request.httpBody = body

        do {
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                return .retry(after: 0, reason: "not an HTTP response")
            }

            switch http.statusCode {
            case 200..<300:
                return .ok

            case 429:
                let header = http.value(forHTTPHeaderField: "Retry-After")
                let seconds = TimeInterval(header?.trimmingCharacters(in: .whitespaces) ?? "") ?? 0
                return .retry(after: max(0, seconds), reason: "rate limited (429)")

            // 404 means this source is not configured there, 400 that the payload is not accepted.
            case 404:
                return .permanent(reason: "unknown source (404)")
            case 400..<500:
                return .permanent(reason: "refused with \(http.statusCode)")

            default:
                return .retry(after: 0, reason: "collector answered \(http.statusCode)")
            }
        } catch {
            return .retry(after: 0, reason: "network error: \(error.localizedDescription)")
        }
    }
}
