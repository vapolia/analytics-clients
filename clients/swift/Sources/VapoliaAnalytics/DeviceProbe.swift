import Foundation

/// The device context, read from the device itself: one setting, no hardware id, no
/// `identifierForVendor`, no IDFA, no geolocation.
enum DeviceProbe {
    static func detect() -> Device {
        Device(country: country)
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
