package com.vapolia.analytics

/** Where transport failures go. Nothing else is ever reported: see [AnalyticsClient.track]. */
interface AnalyticsLogger {
    fun warn(message: String, error: Throwable? = null)
    fun error(message: String, error: Throwable? = null)
}

/**
 * An installation identity that comes from somewhere else — a client being migrated off another
 * SDK, which already has an id and a first-seen date worth keeping.
 */
data class InstallSeed(
    val installId: String,
    /** Milliseconds since the epoch. */
    val issuedAt: Long,
    /** Milliseconds since the epoch. */
    val firstSeen: Long,
)

/**
 * Client configuration. [ingestionUrl] is the only thing without a default.
 *
 * The shape mirrors the .NET client, which is this repository's reference: the few options an app
 * actually sets sit here, the rest in [advanced] and [app].
 */
data class AnalyticsOptions(
    /**
     * The collector's ingestion URL — `https://baseUrl/sourceName`. The last path segment is the
     * source: the app's name, and the Postgres schema it maps to.
     */
    val ingestionUrl: String,

    /**
     * The credential sent as `Authorization: Bearer` — a server token for server-to-server
     * analytics, or a build token for device-to-server analytics. The collector tells the two apart
     * from the token itself, not from how it arrived, so one property covers both roles.
     */
    val token: String? = null,

    /**
     * The identity of this installation, when it comes from somewhere else. Read once, at startup,
     * and only if nothing is stored yet.
     */
    val seedInstallId: (() -> InstallSeed?)? = null,

    /** Toggles collection of analytics. False makes [Analytics.start] a no-op. */
    val enabled: Boolean = true,

    /**
     * ISO 3166-1 alpha-2 countries excluded from the collection. Should be a copy of the exclusion
     * list of the collector (the analytics server).
     */
    val excludedCountries: Set<String> = emptySet(),

    /**
     * Context sent with each batch of events. Every key must be on the source's `context` whitelist.
     * Seeds [Analytics.context], which can be set again later.
     */
    val context: (() -> Map<String, Any?>?)? = null,

    /** Uncommon options. */
    val advanced: AnalyticsAdvancedOptions = AnalyticsAdvancedOptions(),

    /** Uncommon apps-only options. */
    val app: AnalyticsAppOptions = AnalyticsAppOptions(),
) {
    /** The app's name: the URL's last segment, exactly as in the .NET client. */
    val source: String get() = ingestionUrl.trimEnd('/').substringAfterLast('/')
}

/** Uncommon options. The defaults are the ones every client in this repository ships. */
data class AnalyticsAdvancedOptions(
    /** How long events are buffered before they go out. */
    val flushIntervalMs: Long = 30_000,

    /**
     * Max events accepted per [rateWindowMs]. Beyond it everything is dropped until the window ends.
     * Zero disables it. It keeps a buggy installation from getting its whole client address
     * throttled by the collector, which behind a carrier NAT would hit every other installation.
     */
    val maxEventsPerWindow: Int = 30,

    /** The window [maxEventsPerWindow] counts in. Fixed, not sliding. */
    val rateWindowMs: Long = 60_000,

    /** Max events to send per batch. Capped at the collector's own ceiling. */
    val batchSize: Int = MAX_EVENTS_PER_BATCH,

    /** Max events that can wait to be sent. Beyond it [Analytics.track] drops rather than blocks. */
    val queueCapacity: Int = 4_000,

    /** Max attempts to send a batch before it is given up on. */
    val maxAttempts: Int = 3,

    /**
     * `HttpURLConnection` has two timeouts rather than the one .NET's `RequestTimeout` names; both
     * default to the same ten seconds every other client uses.
     */
    val connectTimeoutMs: Int = 10_000,
    val readTimeoutMs: Int = 10_000,

    /** The file the unsent queue is written to. Null is the default, in the app's own files dir. */
    val spoolPath: String? = null,

    /** Events kept on disk across process death. Zero disables the spool. */
    val spoolCapacity: Int = 1_000,

    /** 13 months minus a margin for clock drift — the legal ceiling, with no extension. */
    val installIdLifetimeMs: Long = DEFAULT_LIFETIME_MS,

    /** How long a refusal is remembered. Unlike the identifier, it is refreshed on every visit. */
    val optOutLifetimeMs: Long = DEFAULT_LIFETIME_MS,

    /** Optional. Nothing is logged when null. */
    val logger: AnalyticsLogger? = null,

    /**
     * Called on every loss, next to the log: `(error, reason, permanent)`. Can be used to notify the
     * user that an issue is ongoing.
     */
    val onError: ((Throwable?, String, Boolean) -> Unit)? = null,
)

/** Uncommon apps-only options. */
data class AnalyticsAppOptions(
    /**
     * Whether the client flushes and spools when the app goes to the background — the last moment
     * the process is guaranteed to run.
     */
    val autoFlushOnBackground: Boolean = true,
)

/** 13 months is the legal ceiling, with no extension. The margin absorbs clock drift. */
internal const val DEFAULT_LIFETIME_MS = 390L * 24 * 60 * 60 * 1000

/** A snapshot of what the client did since it was created. */
data class AnalyticsStats(
    /** Queued by `track`. */
    val accepted: Long = 0,
    /** Refused by `track`: opted out, bad install id, excluded country, unusable event name. */
    val rejected: Long = 0,
    /**
     * Lost rather than sent: a saturated rate window, a full queue, a full spool, an event older
     * than the collector accepts, or a permanently refused request.
     */
    val dropped: Long = 0,
    /** Events the collector answered 2xx for. */
    val sent: Long = 0,
    /** Requests issued, retries included. */
    val requests: Long = 0,
)

/**
 * The age of an installation, in buckets, for an app that wants to segment on it.
 *
 * The client keeps the first-seen date — it is the only thing that knows it, and it keeps it across
 * id renewals — but it does not send a bucket on its own: that would be the client naming what is
 * measured. Call this from your [Analytics.context] if the key is on your whitelist.
 */
object InstallAge {
    /** `"0"`, `"1-7"`, `"8-30"`, `"31-90"` or `"90+"`. */
    @JvmStatic
    @JvmOverloads
    fun bucket(firstSeen: Long, now: Long = System.currentTimeMillis()): String {
        val days = (now - firstSeen).toDouble() / (24 * 60 * 60 * 1000)
        return when {
            days < 1 -> "0"
            days < 8 -> "1-7"
            days < 31 -> "8-30"
            days < 91 -> "31-90"
            else -> "90+"
        }
    }
}
