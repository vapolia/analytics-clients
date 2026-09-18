package com.vapolia.analytics

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class SpoolTest {

    @get:Rule
    val folder = TemporaryFolder()

    private fun spool(capacity: Int = 10, file: File = folder.newFile("spool")) = file to Spool(file, capacity)

    private fun pending(name: String, ts: Long = 1_757_500_000_000) = Pending(
        BatchKey(
            "11111111-0000-0000-0000-000011111111",
            Device(platform = "android", country = "FR"),
            """{"plan":"premium"}""",
        ),
        Event(name, ts, mapOf("result" to "win", "moves" to 34.0, "ok" to true)),
    )

    @Test
    fun `round trips events, device context included`() {
        val (_, disk) = spool()
        val items = listOf(pending("app_open"), pending("game_end"))

        disk.save(items)

        assertEquals(items, disk.load())
    }

    @Test
    fun `reading consumes the file`() {
        val (file, disk) = spool()
        disk.save(listOf(pending("app_open")))

        assertEquals(1, disk.load().size)
        assertFalse(file.exists())
        assertTrue(disk.load().isEmpty())
    }

    @Test
    fun `saving nothing clears the file`() {
        val (file, disk) = spool()
        disk.save(listOf(pending("app_open")))

        disk.save(emptyList())

        assertFalse(file.exists())
    }

    @Test
    fun `a truncated file yields nothing and is removed`() {
        val (file, disk) = spool()
        disk.save(listOf(pending("app_open"), pending("game_end")))
        val bytes = file.readBytes()
        file.writeBytes(bytes.copyOf(bytes.size / 2))

        assertTrue(disk.load().isEmpty())
        assertFalse(file.exists())
    }

    @Test
    fun `capacity keeps the oldest events`() {
        val (_, disk) = spool(capacity = 2)

        disk.save(listOf(pending("first"), pending("second"), pending("third")))

        assertEquals(listOf("first", "second"), disk.load().map { it.event.name })
    }
}
