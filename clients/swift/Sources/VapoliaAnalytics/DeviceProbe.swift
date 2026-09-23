import Foundation

/// The device context, read from the device itself: one setting, no hardware id, no
/// `identifierForVendor`, no IDFA, no geolocation.
enum DeviceProbe {
    static func detect() -> Device {
        Device(country: country)
    }

    /// The device locale as a BCP-47 tag, for the consent regime: the UI language and the region
    /// setting, which `Locale.identifier` joins with an underscore rather than a hyphen.
    static var currentLocale: String? {
        let language: String?
        if #available(iOS 16, macCatalyst 16, macOS 13, *) {
            language = Locale.current.language.languageCode?.identifier
        } else {
            language = Locale.current.languageCode
        }

        switch (language, country) {
        case (nil, nil): return nil
        case (nil, let region?): return "und-\(region)"
        case (let language?, nil): return language
        case (let language?, let region?): return "\(language)-\(region)"
        }
    }

    /// The region **setting**, which is what the collector's `country` column holds.
    private static var country: String? {
        if #available(iOS 16, macCatalyst 16, macOS 13, *) {
            return Locale.current.region?.identifier
        } else {
            return Locale.current.regionCode
        }
    }
}
