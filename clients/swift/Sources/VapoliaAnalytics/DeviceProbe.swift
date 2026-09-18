import Foundation

#if canImport(UIKit)
import UIKit
#endif

/// The device context, read from the device itself: a setting or a build constant each, no hardware
/// id, no `identifierForVendor`, no IDFA, no geolocation.
enum DeviceProbe {
    @MainActor
    static func detect() -> Device {
        Device(
            build: Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String,
            platform: platform,
            osVersion: osVersion,
            deviceClass: deviceClass,
            locale: locale,
            country: country,
            // An iOS build is installed from the App Store or from TestFlight; there is no third case
            // worth a column.
            store: "apple"
        )
    }

    private static var platform: String {
        #if targetEnvironment(macCatalyst)
        return "maccatalyst"
        #else
        return "ios"
        #endif
    }

    @MainActor
    private static var osVersion: String {
        #if canImport(UIKit)
        return UIDevice.current.systemVersion
        #else
        return ProcessInfo.processInfo.operatingSystemVersionString
        #endif
    }

    @MainActor
    private static var deviceClass: String {
        #if canImport(UIKit)
        switch UIDevice.current.userInterfaceIdiom {
        case .phone: return "phone"
        case .pad: return "tablet"
        case .mac: return "desktop"
        default: return "other"
        }
        #else
        return "desktop"
        #endif
    }

    /// BCP 47, which is what the collector's `locale` column holds — "fr-FR", not "fr_FR".
    private static var locale: String {
        Locale.current.identifier.replacingOccurrences(of: "_", with: "-")
    }

    private static var country: String? {
        if #available(iOS 16, macCatalyst 16, *) {
            return Locale.current.region?.identifier
        } else {
            return Locale.current.regionCode
        }
    }
}
