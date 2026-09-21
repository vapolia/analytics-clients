package com.vapolia.analytics

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The per-window ceiling is counted in `track`, before anything is queued, so these tests need no
 * collector at all: the transport points at a dead port and nothing is ever flushed.
 */
class RateLimitTest {

    private var now = 1_757_500_000_000L
    private val clients = mutableListOf<AnalyticsClient>()

    @After
    fun tearDown() {
        clients.forEach { it.stop(2_000) }
    }

    private fun client(maxEventsPerWindow: Int): AnalyticsClient = AnalyticsClient(
        options = AnalyticsOptions(
            ingestionUrl = "unused, the transport is built here/testsource",
            advanced = AnalyticsAdvancedOptions(
                flushIntervalMs = 3_600_000,
                maxEventsPerWindow = maxEventsPerWindow,
                rateWindowMs = 60_000,
            ),
        ),
        transport = Transport("http://127.0.0.1:1/testsource", connectTimeoutMs = 200, readTimeoutMs = 200),
        spool = null,
        clock = { now },
    ).also { clients.add(it) }

    private val installId = "11111111-0000-0000-0000-000011111111"
    private val device = Device(country = "FR")

    @Test
    fun `drops everything past the ceiling until the window ends`() {
        val client = client(maxEventsPerWindow = 3)

        repeat(5) { client.track(installId, device, "app_open", null) }

        // Long literals: the counters are Long, and an Int would pick assertEquals(Object, Object).
        assertEquals(3L, client.stats().accepted)
        assertEquals(2L, client.stats().dropped)

        // Still inside the same window.
        now += 59_000
        client.track(installId, device, "app_open", null)
        assertEquals(3L, client.stats().accepted)

        // The window has ended: the count starts again.
        now += 2_000
        client.track(installId, device, "app_open", null)
        assertEquals(4L, client.stats().accepted)
    }

    @Test
    fun `zero disables the ceiling`() {
        val client = client(maxEventsPerWindow = 0)

        repeat(200) { client.track(installId, device, "app_open", null) }

        assertEquals(200L, client.stats().accepted)
        assertEquals(0L, client.stats().dropped)
    }

    @Test
    fun `the ceiling is counted before the event is looked at`() {
        val client = client(maxEventsPerWindow = 1)

        client.track(installId, device, "app_open", null)
        // Refused by the window, not by the sanitizer — an unusable name would count as rejected.
        client.track(installId, device, "   ", null)

        assertEquals(1L, client.stats().accepted)
        assertEquals(1L, client.stats().dropped)
        assertEquals(0L, client.stats().rejected)
    }
}
