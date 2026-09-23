import Foundation

/// The default answer for ``AnalyticsOptions/requiresPriorConsent``, from the regimes listed in
/// OBLIGATIONS.md. Left nil, the client asks this itself, with the device locale.
public enum PriorConsentCountries {
    /// The EEA outside France, Italy, Spain and the Netherlands, plus the United Kingdom.
    public static let all: Set<String> = [
        "AT", "BE", "BG", "CY", "CZ", "DE", "DK", "EE", "FI", "GB", "GR", "HR", "HU",
        "IE", "IS", "LI", "LT", "LU", "LV", "MT", "NO", "PL", "PT", "RO", "SE", "SI", "SK",
    ]
}

/// Whether `locale` — a BCP-47 tag such as `fr-FR`, the device locale — asks before anything may be
/// stored. True when the tag carries no region.
///
/// `fr-CA` answers true and `en-CA` false: Quebec asks first while the rest of Canada does not, and
/// the language is the only signal a locale carries about it. A francophone outside Quebec is asked
/// needlessly, an anglophone inside it is not asked at all.
///
/// A starting point for your own counsel, not a legal opinion. Authority positions move, and this
/// list moves only when the package is updated.
public func localeRequiresPriorConsent(_ locale: String?) -> Bool {
    let (language, region) = Bcp47.split(locale)
    guard let region else { return true }
    if PriorConsentCountries.all.contains(region) { return true }
    return region == "CA" && language?.lowercased() == "fr"
}

/// Enough BCP-47 to read a language and a region off a tag.
enum Bcp47 {

    /// The primary language subtag and the region subtag of `locale`, both nil when absent. The
    /// region is the first 2-letter subtag after the language, which skips a script (`zh-Hant-TW`)
    /// and a UN M.49 code (`es-419`, no region).
    static func split(_ locale: String?) -> (language: String?, region: String?) {
        guard let locale else { return (nil, nil) }

        let parts = locale
            .trimmingCharacters(in: .whitespaces)
            .replacingOccurrences(of: "_", with: "-")
            .split(separator: "-")
            .map(String.init)
        guard let first = parts.first else { return (nil, nil) }

        let language = (2...3).contains(first.count) ? first : nil
        let region = parts.dropFirst().first {
            $0.count == 2 && $0.allSatisfy { $0.isLetter && $0.isASCII }
        }

        return (language, region?.uppercased())
    }
}
