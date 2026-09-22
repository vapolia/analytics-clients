package com.vapolia.analytics

import android.app.Activity
import android.app.Application
import android.content.Context
import android.os.Bundle
import android.util.Log
import java.io.File
import java.util.concurrent.atomic.AtomicInteger

/**
 * The entry point. One call in `Application.onCreate`, one call per event:
 *
 * ```kotlin
 * Analytics.start(this, ingestionUrl = "https://analytics.example.com/myapp")
 * Analytics.track("game_end", "result" to "win", "moves" to 34)
 * ```
 *
 * It holds the installation id, the device and the sender, and emits no event of its own. Use
 * [isFirstRun] and [onForeground] to place your own `first_open` and `app_open`.
 */
object Analytics {

    @Volatile private var client: AnalyticsClient? = null
    @Volatile private var identity: InstallIdentity? = null
    @Volatile private var device: Device = Device()

    /**
     * What is true of the installation for a whole batch. Read again for every event, so an event
     * carries the state it was produced under. Every key must be on the source's `context` whitelist.
     */
    @JvmStatic
    @Volatile
    var context: (() -> Map<String, Any?>?)? = null

    /**
     * Starts the sender. Calling it twice is a no-op: the second call keeps the first configuration.
     * [ingestionUrl] is `https://baseUrl/sourceName` and has no default.
     */
    @JvmStatic
    fun start(context: Context, ingestionUrl: String) =
        start(context, AnalyticsOptions(ingestionUrl = ingestionUrl))

    @JvmStatic
    @Synchronized
    fun start(context: Context, options: AnalyticsOptions) {
        if (client != null) return
        if (!options.enabled) return

        val app = context.applicationContext
        val installIdentity = InstallIdentity(
            prefs = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE),
            idLifetimeMs = options.advanced.installIdLifetimeMs,
            refusalLifetimeMs = options.advanced.optOutLifetimeMs,
            defaultOptedOut = options.defaultOptedOut,
        )
        options.seedInstallId?.invoke()?.let { installIdentity.seed(it) }

        identity = installIdentity
        device = DeviceProbe.detect(app)
        options.context?.let { this.context = it }
        autoFlushOnBackground = options.app.autoFlushOnBackground
        client = AnalyticsClient(
            options = options,
            transport = Transport(
                url = options.ingestionUrl.trimEnd('/'),
                connectTimeoutMs = options.advanced.connectTimeoutMs,
                readTimeoutMs = options.advanced.readTimeoutMs,
                token = options.token,
            ),
            // Zero capacity is how an app turns the spool off, as in the .NET client.
            spool = options.advanced.spoolCapacity.takeIf { it > 0 }?.let {
                Spool(
                    options.advanced.spoolPath?.let(::File) ?: File(app.filesDir, SPOOL_FILE),
                    it,
                )
            },
        )

        // What a previous session spooled must not leave while the person is opted out — or has not
        // yet answered, under a regime that asks first.
        if (installIdentity.optedOut)
            client?.clear(timeoutMs = 0)

        (app as? Application)?.registerActivityLifecycleCallbacks(Lifecycle)
    }

    /**
     * Queues one event. Never blocks, never throws. [name] and every property key must be on the
     * source's whitelist, which this client cannot know.
     */
    @JvmStatic
    @JvmOverloads
    fun track(name: String, props: Map<String, Any?>? = null) {
        val sender = client
        val installIdentity = identity
        if (sender == null || installIdentity == null) {
            if (warnings.getAndIncrement() == 0)
                Log.w(TAG, "Analytics.start() was never called: \"$name\" and the next ones are ignored")
            return
        }

        if (installIdentity.optedOut) return
        val batchContext = encodeContext(context?.invoke(), contextEncoder)
        sender.track(installIdentity.current(), device, name, props, batchContext)
    }

    /** `Analytics.track("game_end", "result" to "win", "moves" to 34)` */
    @JvmStatic
    fun track(name: String, vararg props: Pair<String, Any?>) = track(name, props.toMap())

    /** Sends what is queued, without waiting for it. */
    @JvmStatic
    fun flush() {
        client?.flush(timeoutMs = 0)
    }

    /** Sends what is queued and waits, up to [timeoutMs]. Never call it from the main thread. */
    @JvmStatic
    @JvmOverloads
    fun flushBlocking(timeoutMs: Long = DEFAULT_TIMEOUT_MS): Boolean =
        client?.flush(timeoutMs) ?: false

    /**
     * The right of opposition. Turning it on stops collection, drops what was queued, and forgets the
     * installation id, so opting back in cannot resume the same installation.
     */
    @JvmStatic
    var optedOut: Boolean
        get() = identity?.optedOut ?: false
        set(value) {
            val installIdentity = identity ?: return
            installIdentity.optedOut = value
            if (value) client?.clear(DEFAULT_TIMEOUT_MS)
        }

    /** The current installation id, for a support screen. Null when opted out or not started. */
    @JvmStatic
    val installId: String?
        get() = identity?.takeIf { !it.optedOut }?.current()

    @JvmStatic
    val stats: AnalyticsStats
        get() = client?.stats() ?: AnalyticsStats()

    /**
     * Corrects what the device probe reported — a build number, a device class an app knows better.
     * Not for anything about the app itself, which belongs in [context].
     */
    @JvmStatic
    fun updateDevice(transform: (Device) -> Device) {
        device = transform(device)
    }

    /**
     * Whether this installation has never been seen before — what decides your own `first_open`. Kept
     * apart from the id, so a rotation does not count as a new installation.
     */
    @JvmStatic
    val isFirstRun: Boolean
        get() = identity?.firstRun() ?: false

    /** Records that the installation has been seen. Call it once you emitted your own `first_open`. */
    @JvmStatic
    fun markSeen() {
        identity?.markSeen()
    }

    /**
     * When this installation was first seen, in milliseconds since the epoch, kept across id
     * renewals. Feed it to [InstallAge] if your source whitelists a bucket for it.
     */
    @JvmStatic
    val firstSeen: Long?
        get() = identity?.firstSeen()

    /**
     * Takes an identity issued elsewhere, for a client migrating off another SDK. Only before the
     * client ever issued one of its own; returns whether the seed was taken.
     */
    @JvmStatic
    fun seed(seed: InstallSeed): Boolean = identity?.seed(seed) ?: false

    /**
     * The app coming back to the foreground — where an app names its own `app_open`. It hangs off the
     * activity-lifecycle subscription the client already holds, so a rotation does not fire it.
     */
    @JvmStatic
    @Volatile
    var onForeground: (() -> Unit)? = null

    /** The app leaving the foreground, just before the queue is sent and written down. */
    @JvmStatic
    @Volatile
    var onBackground: (() -> Unit)? = null

    /** One last flush, then the sender stops. What it cannot send is left on disk for the next launch. */
    @JvmStatic
    @JvmOverloads
    @Synchronized
    fun stop(timeoutMs: Long = DEFAULT_TIMEOUT_MS) {
        client?.stop(timeoutMs)
        client = null
    }

    /** Leaving the foreground is the moment to write the queue down: the process may not come back. */
    private object Lifecycle : Application.ActivityLifecycleCallbacks {
        private val started = AtomicInteger()

        override fun onActivityStarted(activity: Activity) {
            if (started.getAndIncrement() == 0)
                runCatching { onForeground?.invoke() }
        }

        override fun onActivityStopped(activity: Activity) {
            if (started.decrementAndGet() == 0) {
                runCatching { onBackground?.invoke() }
                if (autoFlushOnBackground) client?.flush(timeoutMs = 0, persist = true)
            }
        }

        override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) = Unit
        override fun onActivityResumed(activity: Activity) = Unit
        override fun onActivityPaused(activity: Activity) = Unit
        override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) = Unit
        override fun onActivityDestroyed(activity: Activity) = Unit
    }

    @Volatile private var autoFlushOnBackground = true

    private val contextEncoder = BatchEncoder()

    private val warnings = AtomicInteger()

    private const val TAG = "Analytics"
    private const val PREFS = "vapolia.analytics"
    private const val SPOOL_FILE = "vapolia-analytics.spool"
    private const val DEFAULT_TIMEOUT_MS = 3_000L
}

/** Sends the client's own failures to logcat. Pass it as [AnalyticsAdvancedOptions.logger] while integrating. */
object LogcatLogger : AnalyticsLogger {
    override fun warn(message: String, error: Throwable?) {
        Log.w("Analytics", message, error)
    }

    override fun error(message: String, error: Throwable?) {
        Log.e("Analytics", message, error)
    }
}
