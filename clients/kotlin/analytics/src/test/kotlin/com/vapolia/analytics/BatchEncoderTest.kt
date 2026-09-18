package com.vapolia.analytics

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class BatchEncoderTest {

    private val installId = "11111111-0000-0000-0000-000011111111"
    private val encoder = BatchEncoder()

    @Test
    fun `encodes the batch shape the collector expects`() {
        val key = BatchKey(installId, Device(platform = "android", country = "FR"), """{"plan":"free"}""")
        val json = encoder.encode(
            key,
            listOf(
                Event("app_open", 1_757_500_000_000, emptyMap()),
                Event("game_end", 1_757_500_001_000, mapOf("result" to "win", "moves" to 34.0)),
            ),
        )

        assertTrue(json.startsWith("{\"installId\":\"$installId\""))
        assertTrue(json.contains("\"platform\":\"android\""))
        assertTrue(json.contains("\"country\":\"FR\""))
        assertTrue(json.contains("\"context\":{\"plan\":\"free\"}"))
        assertTrue(json.contains("\"name\":\"game_end\""))
        assertTrue(json.contains("\"props\":{\"result\":\"win\",\"moves\":34}"))
        // Nothing is sent for a field the device did not fill in.
        assertFalse(json.contains("osVersion"))
        assertFalse(json.contains("\"props\":{}"))
    }

    @Test
    fun `timestamps are iso 8601 in utc`() {
        val json = encoder.encode(BatchKey(installId, Device()), listOf(Event("app_open", 0, emptyMap())))

        assertTrue(json, json.contains("\"ts\":\"1970-01-01T00:00:00.000Z\""))
    }

    @Test
    fun `strings are escaped`() {
        val json = encoder.encode(
            BatchKey(installId, Device()),
            listOf(Event("screen_view", 0, mapOf("name" to "a\"b\\c\nd"))),
        )

        assertTrue(json, json.contains("\"name\":\"a\\\"b\\\\c\\nd\""))
    }

    @Test
    fun `whole numbers keep no fractional part`() {
        val json = encoder.encode(
            BatchKey(installId, Device()),
            listOf(Event("game_end", 0, mapOf("moves" to 34.0, "ratio" to 0.5))),
        )

        assertTrue(json, json.contains("\"moves\":34"))
        assertTrue(json, json.contains("\"ratio\":0.5"))
    }

    @Test
    fun `one request carries one install id`() {
        val json = encoder.encode(BatchKey(installId, Device()), listOf(Event("app_open", 0, emptyMap())))

        assertEquals(1, json.split("installId").size - 1)
    }
}
