import Foundation

/// Facts about the device, which the collector stores per event but which only change between
/// launches. Every field is optional, and `Analytics` fills most of them in. What is true of the
/// installation rather than of the device belongs in the batch context instead.
public struct Device: Hashable, Codable, Sendable {
    /// Store build number, not the display version.
    public var build: String?
    /// android | ios | maccatalyst | windows | web
    public var platform: String?
    public var osVersion: String?
    /// phone | tablet | desktop | other
    public var deviceClass: String?
    public var locale: String?
    /// ISO 3166-1 alpha-2, from the device locale.
    public var country: String?
    /// google | apple | other
    public var store: String?

    public init(
        build: String? = nil,
        platform: String? = nil,
        osVersion: String? = nil,
        deviceClass: String? = nil,
        locale: String? = nil,
        country: String? = nil,
        store: String? = nil
    ) {
        self.build = build
        self.platform = platform
        self.osVersion = osVersion
        self.deviceClass = deviceClass
        self.locale = locale
        self.country = country
        self.store = store
    }

    /// The device reduced to what the collector will store, or nil when the whole batch is refused.
    func cleaned(excluding excludedCountries: Set<String> = []) -> Device? {
        let country = Clean.country(country)
        if let country, excludedCountries.contains(country) { return nil }

        return Device(
            build: Clean.text(build, maxLength: Limits.maxBuildLength),
            platform: Clean.pick(platform, from: Clean.platforms),
            osVersion: Clean.text(osVersion, maxLength: Limits.maxOsVersionLength),
            deviceClass: Clean.pick(deviceClass, from: Clean.deviceClasses),
            locale: Clean.text(locale, maxLength: Limits.maxLocaleLength),
            country: country,
            store: Clean.pick(store, from: Clean.stores)
        )
    }
}
