package com.vapolia.analytics

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** tz travels with each event, in minutes east of UTC, apart from ts which stays UTC. */
class TzTest {
    private val installId = "11111111-0000-0000-0000-000011111111"

    @Test
    fun encodesTheOffsetInMinutesPerEvent() {
        val json = BatchEncoder().encode(
            BatchKey(installId, Device(platform = "android")),
            listOf(Event("app_open", 0L, emptyMap(), 120), Event("game_end", 1L, emptyMap(), -570)),
        )

        assertTrue(json, json.contains("\"tz\":120"))
        assertTrue(json, json.contains("\"tz\":-570"))
    }

    @Test
    fun omitsAnUnknownOffset() {
        val json = BatchEncoder().encode(BatchKey(installId, Device()), listOf(Event("app_open", 0L, emptyMap())))

        assertFalse(json, json.contains("\"tz\""))
    }

    @Test
    fun theSpoolKeepsTheOffset() {
        val file = File.createTempFile("tz-spool", ".bin").apply { deleteOnExit() }
        val spool = Spool(file, 10)
        val items = listOf(
            Pending(BatchKey(installId, Device(platform = "android")), Event("app_open", 1_757_500_000_000, emptyMap(), -570)),
            Pending(BatchKey(installId, Device(platform = "android")), Event("game_end", 1_757_500_001_000, emptyMap())),
        )

        spool.save(items)

        assertEquals(items, spool.load())
    }
}
