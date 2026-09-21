package com.vapolia.analytics

/**
 * What the body still says about the device: its region, and nothing else.
 *
 * The platform, the build, the OS version, the device class and the store come from the
 * `Authorization` token, which names the build that was issued it — so no client sends them.
 * [country] stays because it is the axis of the source's `excludedCountries` filter. Same shape as
 * the .NET client's `Device` record.
 */
data class Device(
    /** ISO 3166-1 alpha-2, from the device's **region setting** — never a geolocation of the IP. */
    val country: String? = null,
) {
    /** The device reduced to what the collector will store, or null when the whole batch is refused. */
    internal fun clean(excludedCountries: Set<String> = emptySet()): Device? {
        val country = Clean.country(country)
        if (country != null && excludedCountries.any { it.equals(country, ignoreCase = true) })
            return null

        return Device(country = country)
    }
}
