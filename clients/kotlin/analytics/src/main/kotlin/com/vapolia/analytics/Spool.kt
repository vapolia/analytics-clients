package com.vapolia.analytics

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File

/**
 * What survives the process being killed — the common case on Android, where a backgrounded app is
 * reclaimed without notice. The format is binary rather than JSON: this class is its only reader,
 * and parsing JSON back would mean a dependency.
 *
 * Events are read back once: [load] deletes the file. A duplicated event is a wrong count, a lost
 * one only a missing count, so the ambiguity is resolved towards losing.
 */
internal class Spool(private val file: File, private val capacity: Int) {

    fun save(items: List<Pending>) {
        if (items.isEmpty()) {
            clear()
            return
        }

        // Beyond the cap the oldest are kept: they are the ones an app restart is meant to recover.
        val kept = if (items.size > capacity) items.subList(0, capacity) else items

        val temp = File(file.parentFile, file.name + ".tmp")
        try {
            DataOutputStream(temp.outputStream().buffered()).use { out ->
                out.writeInt(MAGIC)
                out.writeInt(VERSION)
                out.writeInt(kept.size)
                for (item in kept) write(out, item)
            }
            if (file.exists()) file.delete()
            if (!temp.renameTo(file)) temp.delete()
        } catch (e: Exception) {
            temp.delete()
            throw e
        }
    }

    fun load(): List<Pending> {
        if (!file.exists())
            return emptyList()

        return try {
            DataInputStream(file.inputStream().buffered()).use { input ->
                if (input.readInt() != MAGIC || input.readInt() != VERSION)
                    return@use emptyList()

                val count = input.readInt()
                if (count <= 0 || count > capacity)
                    return@use emptyList()

                ArrayList<Pending>(count).apply {
                    repeat(count) { add(read(input)) }
                }
            }
        } catch (_: Exception) {
            // A truncated file is a process killed mid-write. Its events are not worth a recovery path.
            emptyList()
        } finally {
            clear()
        }
    }

    fun clear() {
        if (file.exists()) file.delete()
    }

    private fun write(out: DataOutputStream, item: Pending) {
        out.writeUTF(item.key.installId)

        val country = item.key.device.country
        out.writeBoolean(country != null)
        if (country != null) out.writeUTF(country)
        out.writeUTF(item.key.context)

        out.writeLong(item.event.tsMillis)
        out.writeBoolean(item.event.tzMinutes != null)
        item.event.tzMinutes?.let { out.writeShort(it) }
        out.writeUTF(item.event.name)
        out.writeInt(item.event.props.size)
        for ((key, value) in item.event.props) {
            out.writeUTF(key)
            when (value) {
                is String -> { out.writeByte(TYPE_STRING); out.writeUTF(value) }
                is Boolean -> { out.writeByte(TYPE_BOOL); out.writeBoolean(value) }
                else -> { out.writeByte(TYPE_NUMBER); out.writeDouble((value as Number).toDouble()) }
            }
        }
    }

    private fun read(input: DataInputStream): Pending {
        val installId = input.readUTF()

        val device = Device(country = if (input.readBoolean()) input.readUTF() else null)
        val context = input.readUTF()

        val ts = input.readLong()
        val tz = if (input.readBoolean()) input.readShort().toInt() else null
        val name = input.readUTF()
        val propCount = input.readInt()
        val props = LinkedHashMap<String, Any>(propCount.coerceAtMost(MAX_PROPS_PER_EVENT))
        repeat(propCount) {
            val key = input.readUTF()
            when (input.readByte().toInt()) {
                TYPE_STRING -> props[key] = input.readUTF()
                TYPE_BOOL -> props[key] = input.readBoolean()
                else -> props[key] = input.readDouble()
            }
        }

        return Pending(BatchKey(installId, device, context), Event(name, ts, props, tz))
    }

    private companion object {
        const val MAGIC = 0x56414E31 // "VAN1"
        // Bumped with every layout change (2: batch context, 3: tz). A file written by an older layout is dropped, not read.
        const val VERSION = 4
        const val TYPE_STRING = 1
        const val TYPE_BOOL = 2
        const val TYPE_NUMBER = 3
    }
}
