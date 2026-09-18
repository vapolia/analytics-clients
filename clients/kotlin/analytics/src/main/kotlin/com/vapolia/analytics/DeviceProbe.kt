package com.vapolia.analytics

import android.content.Context
import android.os.Build
import java.util.Locale

/**
 * The device context, read from the device itself.
 *
 * Everything here is a setting or a build constant: no hardware id, no advertising id, and no
 * geolocation — [Device.country] is the user's region setting, which is what the exemption requires.
 */
internal object DeviceProbe {

    fun detect(context: Context): Device {
        val locale = Locale.getDefault()
        return Device(
            build = versionCode(context),
            platform = "android",
            osVersion = Build.VERSION.RELEASE,
            deviceClass = if (context.resources.configuration.smallestScreenWidthDp >= TABLET_DP) "tablet" else "phone",
            locale = locale.toLanguageTag(),
            country = locale.country,
            store = store(context),
        )
    }

    private fun versionCode(context: Context): String? = runCatching {
        val info = context.packageManager.getPackageInfo(context.packageName, 0)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P)
            info.longVersionCode.toString()
        else
            @Suppress("DEPRECATION") info.versionCode.toString()
    }.getOrNull()

    /** Play, or anything else. A sideloaded build is "other", never unknown-but-assumed-Play. */
    private fun store(context: Context): String = runCatching {
        val installer = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R)
            context.packageManager.getInstallSourceInfo(context.packageName).installingPackageName
        else
            @Suppress("DEPRECATION") context.packageManager.getInstallerPackageName(context.packageName)

        if (installer == PLAY_STORE) "google" else "other"
    }.getOrDefault("other")

    private const val PLAY_STORE = "com.android.vending"

    /** The usual tablet threshold, the same one the resource qualifier `sw600dp` uses. */
    private const val TABLET_DP = 600
}
