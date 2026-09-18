package com.vapolia.analytics

/** Where transport failures go. Nothing else is ever reported: see [AnalyticsClient.track]. */
interface AnalyticsLogger {
    fun warn(message: String, error: Throwable? = null)
    fun error(message: String, error: Throwable? = null)
}

/**
 * Everything the client needs beyond the app's identity. [source] and [endpoint] have no default.
 * The other defaults are tuned for a phone.
 */
data class AnalyticsConfig(
    /** The app's name: the URL segment, and the Postgres schema it maps to. */
    val source: String,

    /** The collector's base URL, e.g. "https://analytics.example.com". The source is appended to it. */
    val endpoint: String,

    /** How often pending events are sent even when no batch is full. */
    val flushIntervalMs: Long = 30_000,

    /** Events of one (install, device) group per request. Capped at the collector's own ceiling. */
    val batchSize: Int = MAX_EVENTS_PER_BATCH,

    /** Events waiting for the sender. Beyond it [Analytics.track] drops rather than blocks. */
    val queueCapacity: Int = 2_000,

    /**
     * Events accepted per [rateWindowMs]. Beyond it everything is dropped until the window ends. It
     * keeps a buggy installation from getting its whole client address throttled by the collector,
     * which behind a carrier NAT would hit every other installation too. Zero disables it.
     */
    val maxEventsPerWindow: Int = 30,

    /** The window [maxEventsPerWindow] counts in. Fixed, not sliding. */
    val rateWindowMs: Long = 60_000,

    /** Events kept on disk across process death. */
    val spoolCapacity: Int = 1_000,

    /** Attempts per request before a batch is given up on (kept for later if the failure was transient). */
    val maxAttempts: Int = 3,

    val connectTimeoutMs: Int = 10_000,
    val readTimeoutMs: Int = 10_000,

    /**
     * The token issued for this build by the collector's admin service, as a CI step, and embedded in
     * the app. It replaces the platform and build number in every batch, so a build cannot be invented
     * and can be excluded. Public by nature, not a credential. A source that requires one answers 401
     * without.
     */
    val buildToken: String? = null,

    /**
     * ISO 3166-1 alpha-2 countries not measured at all: nothing is sent from a device whose region is
     * one of them. Copy the source's `excludedCountries`.
     */
    val excludedCountries: Set<String> = emptySet(),

    /** Optional. Nothing is logged when null. */
    val logger: AnalyticsLogger? = null,
)

/** A snapshot of what the client did since it was created. */
data class AnalyticsStats(
    /** Queued by `track`. */
    val accepted: Long = 0,
    /** Refused by `track`: opted out, bad install id, excluded country, unusable event name. */
    val rejected: Long = 0,
    /** Accepted then lost: full queue, full spool, or a permanently refused request. */
    val dropped: Long = 0,
    /** Events the collector answered 2xx for. */
    val sent: Long = 0,
    /** Requests issued, retries included. */
    val requests: Long = 0,
)
