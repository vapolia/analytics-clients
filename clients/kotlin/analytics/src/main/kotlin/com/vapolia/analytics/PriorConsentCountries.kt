package com.vapolia.analytics

/**
 * The default answer for [AnalyticsOptions.requiresPriorConsent], from the regimes listed in
 * OBLIGATIONS.md. Left null, the client asks this itself, with the device locale.
 *
 * The EEA outside France, Italy, Spain and the Netherlands, plus the United Kingdom.
 */
val priorConsentCountries: Set<String> = setOf(
    "AT", "BE", "BG", "CY", "CZ", "DE", "DK", "EE", "FI", "GB", "GR", "HR", "HU",
    "IE", "IS", "LI", "LT", "LU", "LV", "MT", "NO", "PL", "PT", "RO", "SE", "SI", "SK",
)

/**
 * Whether [locale] — a BCP-47 tag such as `fr-FR`, the device locale — asks before anything may be
 * stored. True when the tag carries no region.
 *
 * `fr-CA` answers true and `en-CA` false: Quebec asks first while the rest of Canada does not, and
 * the language is the only signal a locale carries about it. A francophone outside Quebec is asked
 * needlessly, an anglophone inside it is not asked at all.
 *
 * A starting point for your own counsel, not a legal opinion. Authority positions move, and this list
 * moves only when the library is updated.
 */
fun localeRequiresPriorConsent(locale: String?): Boolean {
    val (language, region) = Bcp47.split(locale)
    return when {
        region == null -> true
        region in priorConsentCountries -> true
        else -> region == "CA" && language.equals("fr", ignoreCase = true)
    }
}

/** Enough BCP-47 to read a language and a region off a tag. */
internal object Bcp47 {

    /**
     * The primary language subtag and the region subtag of [locale], both null when absent. The
     * region is the first 2-letter subtag after the language, which skips a script (`zh-Hant-TW`)
     * and a UN M.49 code (`es-419`, no region).
     */
    fun split(locale: String?): Pair<String?, String?> {
        if (locale.isNullOrBlank()) return null to null

        val parts = locale.trim().replace('_', '-').split('-').filter { it.isNotEmpty() }
        if (parts.isEmpty()) return null to null
        val language = parts[0].takeIf { it.length in 2..3 }
        val region = parts.drop(1).firstOrNull { it.length == 2 && it.all(Char::isLetter) }

        return language to region?.uppercase()
    }
}
