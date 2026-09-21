import Foundation

/// What the body still says about the device: its region, and nothing else.
///
/// The platform, the build, the OS version, the device class and the store come from the
/// `Authorization` token, which names the build that was issued it — so a build cannot be invented,
/// and no client sends them. `country` stays here because it is the axis of the source's
/// `excludedCountries` filter. Same shape as the .NET client's `Device` record.
public struct Device: Hashable, Codable, Sendable {
    /// ISO 3166-1 alpha-2, from the device's **region setting** — never a geolocation of the IP.
    public var country: String?

    public init(country: String? = nil) {
        self.country = country
    }

    /// The device reduced to what the collector will store, or nil when the whole batch is refused.
    func cleaned(excluding excludedCountries: Set<String> = []) -> Device? {
        let country = Clean.country(country)
        if let country, excludedCountries.contains(country) { return nil }

        return Device(country: country)
    }
}
