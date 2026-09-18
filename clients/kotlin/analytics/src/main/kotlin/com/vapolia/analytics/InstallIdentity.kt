package com.vapolia.analytics

import android.content.SharedPreferences
import java.util.UUID

/**
 * The installation id, and the two flags that go with it. The id is random, local, and renewed on
 * its own: it is never derived from an account, a device id or an advertising id.
 */
internal class InstallIdentity(
    private val prefs: SharedPreferences,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    /** The current id, reissued when the old one reached its ceiling. */
    fun current(): String {
        val now = clock()
        val id = prefs.getString(KEY_ID, null)
        val issuedAt = prefs.getLong(KEY_ISSUED_AT, 0)

        // A device clock moved backwards would otherwise freeze the id: reissue rather than extend.
        val expired = issuedAt <= 0 || now - issuedAt >= MAX_AGE_MS || issuedAt > now + DAY_MS
        if (id != null && Clean.installId(id) != null && !expired)
            return id

        val fresh = UUID.randomUUID().toString()
        prefs.edit().putString(KEY_ID, fresh).putLong(KEY_ISSUED_AT, now).apply()
        return fresh
    }

    /**
     * Whether this installation has never been seen before — what an app's own `first_open` hangs on.
     * Kept apart from the id: a rotation is not a new installation.
     */
    fun firstRun(): Boolean = !prefs.getBoolean(KEY_SEEN, false)

    fun markSeen() {
        prefs.edit().putBoolean(KEY_SEEN, true).apply()
    }

    var optedOut: Boolean
        get() = prefs.getBoolean(KEY_OPTED_OUT, false)
        set(value) {
            val editor = prefs.edit().putBoolean(KEY_OPTED_OUT, value)
            // Opting out forgets the id: opting back in later must not resume the same installation.
            if (value) editor.remove(KEY_ID).remove(KEY_ISSUED_AT)
            editor.apply()
        }

    private companion object {
        const val KEY_ID = "install_id"
        const val KEY_ISSUED_AT = "install_id_issued_at"
        const val KEY_SEEN = "first_open_sent"
        const val KEY_OPTED_OUT = "opted_out"

        const val DAY_MS = 24L * 60 * 60 * 1000

        /** 13 months is the legal ceiling, with no extension. The margin absorbs clock drift. */
        const val MAX_AGE_MS = 390 * DAY_MS
    }
}
