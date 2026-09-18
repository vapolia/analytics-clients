package com.vapolia.analytics

/**
 * Facts about the device, which the collector stores per event but which only change between
 * launches. All fields are optional, and [Analytics] fills most of them in. What is true of the
 * installation rather than of the device belongs in the batch context instead.
 */
data class Device(
    /** Store build number, not the display version. */
    val build: String? = null,
    /** android | ios | maccatalyst | windows | web */
    val platform: String? = null,
    val osVersion: String? = null,
    /** phone | tablet | desktop | other */
    val deviceClass: String? = null,
    val locale: String? = null,
    /** ISO 3166-1 alpha-2, from the device locale. */
    val country: String? = null,
    /** google | apple | other */
    val store: String? = null,
) {
    /** The device reduced to what the collector will store, or null when the whole batch is refused. */
    internal fun clean(excludedCountries: Set<String> = emptySet()): Device? {
        val country = Clean.country(country)
        if (country != null && excludedCountries.any { it.equals(country, ignoreCase = true) })
            return null

        return Device(
            build = Clean.text(build, MAX_BUILD_LENGTH),
            platform = Clean.pick(platform, Clean.PLATFORMS),
            osVersion = Clean.text(osVersion, MAX_OS_VERSION_LENGTH),
            deviceClass = Clean.pick(deviceClass, Clean.DEVICE_CLASSES),
            locale = Clean.text(locale, MAX_LOCALE_LENGTH),
            country = country,
            store = Clean.pick(store, Clean.STORES),
        )
    }
}
