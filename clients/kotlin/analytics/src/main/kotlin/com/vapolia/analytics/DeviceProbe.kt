package com.vapolia.analytics

import android.content.Context
import java.util.Locale

/**
 * The device context, read from the device itself: one setting, no hardware id, no advertising id,
 * and no geolocation — [Device.country] is the user's region setting, which is what the exemption
 * requires.
 */
internal object DeviceProbe {

    fun detect(@Suppress("UNUSED_PARAMETER") context: Context): Device =
        Device(country = Locale.getDefault().country)

    /** The device locale as a BCP-47 tag, for the consent regime. */
    fun currentLocale(): String? = Locale.getDefault().toLanguageTag().takeIf { it != "und" }
}
