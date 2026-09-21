package com.vapolia.analytics

import android.content.SharedPreferences
import java.util.UUID

/**
 * The installation id, and the flags that go with it. The id is random, local, and renewed on its
 * own: it is never derived from an account, a device id or an advertising id.
 */
internal class InstallIdentity(
    private val prefs: SharedPreferences,
    private val clock: () -> Long = System::currentTimeMillis,
    private val idLifetimeMs: Long = DEFAULT_LIFETIME_MS,
    private val refusalLifetimeMs: Long = DEFAULT_LIFETIME_MS,
) {
    /** The current id, reissued when the old one reached its ceiling. */
    fun current(): String {
        val now = clock()
        val id = prefs.getString(KEY_ID, null)
        val issuedAt = prefs.getLong(KEY_ISSUED_AT, 0)

        // The date this installation was first seen, kept across id renewals: a renewal is not a new
        // installation, and counting it as one would inflate installs every 13 months.
        if (prefs.getLong(KEY_FIRST_SEEN, 0) <= 0)
            prefs.edit().putLong(KEY_FIRST_SEEN, now).apply()

        // A device clock moved backwards would otherwise freeze the id: reissue rather than extend.
        val expired = issuedAt <= 0 || now - issuedAt >= idLifetimeMs || issuedAt > now + DAY_MS
        if (id != null && Clean.installId(id) != null && !expired)
            return id

        val fresh = UUID.randomUUID().toString()
        prefs.edit().putString(KEY_ID, fresh).putLong(KEY_ISSUED_AT, now).apply()
        return fresh
    }

    /**
     * Takes an identity issued elsewhere, unless this installation already has one. Returns whether
     * the seed was taken, so a caller can tell a migration from a no-op.
     */
    fun seed(seed: InstallSeed): Boolean {
        if (prefs.getString(KEY_ID, null) != null) return false
        val id = Clean.installId(seed.installId) ?: return false

        prefs.edit()
            .putString(KEY_ID, id)
            .putLong(KEY_ISSUED_AT, seed.issuedAt)
            .putLong(KEY_FIRST_SEEN, seed.firstSeen)
            .apply()
        return true
    }

    /** When the installation was first seen, or null before the first id was issued. */
    fun firstSeen(): Long? = prefs.getLong(KEY_FIRST_SEEN, 0).takeIf { it > 0 }

    /**
     * Whether this installation has never been seen before — what an app's own `first_open` hangs on.
     * Kept apart from the id: a rotation is not a new installation.
     */
    fun firstRun(): Boolean = !prefs.getBoolean(KEY_SEEN, false)

    fun markSeen() {
        prefs.edit().putBoolean(KEY_SEEN, true).apply()
    }

    /**
     * A refusal is remembered for [refusalLifetimeMs] and — unlike the identifier — refreshed on
     * every read: an opposition must not quietly lapse while the app is still in use.
     */
    var optedOut: Boolean
        get() {
            if (!prefs.getBoolean(KEY_OPTED_OUT, false)) return false

            val now = clock()
            val recordedAt = prefs.getLong(KEY_OPTED_OUT_AT, 0).takeIf { it > 0 } ?: now
            if (now - recordedAt >= refusalLifetimeMs) {
                prefs.edit().remove(KEY_OPTED_OUT).remove(KEY_OPTED_OUT_AT).apply()
                return false
            }

            prefs.edit().putLong(KEY_OPTED_OUT_AT, now).apply()
            return true
        }
        set(value) {
            val editor = prefs.edit().putBoolean(KEY_OPTED_OUT, value)
            if (value) {
                editor.putLong(KEY_OPTED_OUT_AT, clock())
                // Opting out forgets the id: opting back in later must not resume the same install.
                editor.remove(KEY_ID).remove(KEY_ISSUED_AT)
            } else {
                editor.remove(KEY_OPTED_OUT_AT)
            }
            editor.apply()
        }

    private companion object {
        const val KEY_ID = "install_id"
        const val KEY_ISSUED_AT = "install_id_issued_at"
        const val KEY_FIRST_SEEN = "first_seen"
        const val KEY_SEEN = "first_open_sent"
        const val KEY_OPTED_OUT = "opted_out"
        const val KEY_OPTED_OUT_AT = "opted_out_at"

        const val DAY_MS = 24L * 60 * 60 * 1000
    }
}
