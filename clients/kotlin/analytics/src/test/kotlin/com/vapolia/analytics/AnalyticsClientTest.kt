package com.vapolia.analytics

import com.sun.net.httpserver.HttpServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.LinkedBlockingQueue

/** The collector, reduced to what a client can observe: the bodies it received, and what it answered. */
private class FakeCollector {
    private val server: HttpServer = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
    private val bodies = ConcurrentLinkedQueue<String>()

    /** Consumed in order; 204, like the real one, once exhausted. */
    val statuses = LinkedBlockingQueue<Int>()

    init {
        server.createContext("/testsource") { exchange ->
            bodies.add(exchange.requestBody.readBytes().toString(Charsets.UTF_8))
            exchange.sendResponseHeaders(statuses.poll() ?: 204, -1)
            exchange.close()
        }
        server.start()
    }

    val url: String get() = "http://127.0.0.1:${server.address.port}/testsource"

    fun received(): List<String> = bodies.toList()

    fun stop() = server.stop(0)
}

class AnalyticsClientTest {

    @get:Rule
    val folder = TemporaryFolder()

    private val installId = "11111111-0000-0000-0000-000011111111"
    private val otherInstallId = "22222222-0000-0000-0000-000022222222"

    private lateinit var collector: FakeCollector
    private var now = 1_757_500_000_000L
    private val clients = mutableListOf<AnalyticsClient>()

    @Before
    fun setUp() {
        collector = FakeCollector()
    }

    @After
    fun tearDown() {
        clients.forEach { it.stop(2_000) }
        collector.stop()
    }

    private fun client(
        spool: Spool? = null,
        queueCapacity: Int = 2_000,
        batchSize: Int = MAX_EVENTS_PER_BATCH,
        maxAttempts: Int = 3,
        url: String = collector.url,
    ): AnalyticsClient = AnalyticsClient(
        config = AnalyticsConfig(
            excludedCountries = setOf("KR"),
            source = "testsource",
            endpoint = "unused, the transport is built here",
            flushIntervalMs = 3_600_000, // tests flush explicitly
            batchSize = batchSize,
            queueCapacity = queueCapacity,
            maxAttempts = maxAttempts,
        ),
        transport = Transport(url, connectTimeoutMs = 2_000, readTimeoutMs = 2_000),
        spool = spool,
        clock = { now },
    ).also { clients.add(it) }

    private fun AnalyticsClient.flushOrFail(persist: Boolean = false) {
        assertTrue("flush timed out", flush(timeoutMs = 10_000, persist = persist))
    }

    @Test
    fun `sends one batch carrying the global properties`() {
        val client = client()
        val device = Device(platform = "Android", country = "fr", deviceClass = "phone")

        val context = """{"plan":"premium"}"""
        client.track(installId, device, "app_open", null, context)
        client.track(installId, device, "game_end", mapOf("result" to "win", "moves" to 34), context)
        client.flushOrFail()

        val bodies = collector.received()
        assertEquals(1, bodies.size)
        val body = bodies.single()
        assertTrue(body, body.contains("\"installId\":\"$installId\""))
        assertTrue(body, body.contains("\"platform\":\"android\""))
        assertTrue(body, body.contains("\"country\":\"FR\""))
        assertTrue(body, body.contains("\"context\":{\"plan\":\"premium\"}"))
        assertTrue(body, body.contains("\"moves\":34"))
        assertEquals(2, client.stats().sent)
    }

    @Test
    fun `groups by install id and device`() {
        val client = client()

        client.track(installId, Device(platform = "android"), "app_open", null)
        client.track(installId, Device(platform = "android"), "game_start", null)
        client.track(installId, Device(platform = "android", build = "42"), "app_open", null)
        client.track(otherInstallId, Device(platform = "android"), "app_open", null)
        client.flushOrFail()

        assertEquals(3, collector.received().size)
    }

    @Test
    fun `sends as soon as a batch is full`() {
        val client = client(batchSize = 5)

        repeat(5) { client.track(installId, Device(), "app_open", null) }

        val deadline = System.currentTimeMillis() + 5_000
        while (collector.received().isEmpty() && System.currentTimeMillis() < deadline)
            Thread.sleep(10)

        assertEquals(1, collector.received().size)
    }

    @Test
    fun `refuses what the collector would drop`() {
        val client = client()

        val refused = listOf(
            Triple("", Device(), "app_open"),
            Triple("not-a-uuid", Device(), "app_open"),
            Triple("00000000-0000-0000-0000-000000000000", Device(), "app_open"),
            Triple(installId, Device(country = "KR"), "app_open"),
            Triple(installId, Device(country = "kr"), "app_open"),
            Triple(installId, Device(), "   "),
        )
        refused.forEach { (id, device, name) -> client.track(id, device, name, null) }
        client.flushOrFail()

        assertEquals(0, collector.received().size)
        assertEquals(refused.size.toLong(), client.stats().rejected)
    }

    @Test
    fun `keeps events a transient failure left unsent`() {
        val client = client(maxAttempts = 2)
        repeat(2) { collector.statuses.add(500) }

        client.track(installId, Device(), "app_open", null)
        client.flushOrFail()

        val stats = client.stats()
        assertEquals(2, stats.requests)
        assertEquals(0, stats.sent)
        assertEquals("a phone that is offline keeps its events", 0, stats.dropped.toInt())

        // The next flush finds the collector back and sends the same event.
        client.flushOrFail()
        assertEquals(1, client.stats().sent)
    }

    @Test
    fun `drops what a retry cannot fix`() {
        val client = client()
        collector.statuses.add(404)

        client.track(installId, Device(), "app_open", null)
        client.flushOrFail()

        assertEquals(1, collector.received().size)
        assertEquals(1, client.stats().dropped)
    }

    @Test
    fun `drops events older than the collector accepts`() {
        val client = client()

        client.track(installId, Device(), "app_open", null)
        now += MAX_EVENT_AGE_MS + 1
        client.flushOrFail()

        assertEquals(0, collector.received().size)
        assertEquals(1, client.stats().dropped)
    }

    @Test
    fun `a full queue drops instead of blocking`() {
        val client = client(queueCapacity = 1, url = "http://127.0.0.1:1/testsource")

        repeat(200) { client.track(installId, Device(), "app_open", null) }

        val stats = client.stats()
        assertEquals(200, stats.accepted + stats.dropped)
    }

    @Test
    fun `what could not be sent is spooled and sent on the next launch`() {
        val file = folder.newFile("spool")
        val failing = client(spool = Spool(file, 100), maxAttempts = 1, url = "http://127.0.0.1:1/testsource")

        failing.track(installId, Device(platform = "android"), "game_end", mapOf("result" to "win"))
        assertTrue(failing.stop(10_000))
        assertTrue("the queue should have been written down", file.exists())

        val restarted = client(spool = Spool(file, 100))
        restarted.flushOrFail()

        val body = collector.received().single()
        assertTrue(body, body.contains("\"name\":\"game_end\""))
        assertTrue(body, body.contains("\"result\":\"win\""))
    }
}
