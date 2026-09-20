package com.vapolia.analytics

import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/** [tzMinutes]: minutes east of UTC when the event was tracked, apart from [tsMillis] which is UTC. Null when unknown. */
internal data class Event(val name: String, val tsMillis: Long, val props: Map<String, Any>, val tzMinutes: Int? = null)

/**
 * What groups events into one request: one installation, one device, one batch context. The context
 * is carried as its canonical JSON text, so identical contexts compare equal.
 */
internal data class BatchKey(val installId: String, val device: Device, val context: String = "{}")

internal data class Pending(val key: BatchKey, val event: Event)

/**
 * The wire shape of `POST /{source}`. Hand-written rather than a JSON library: the payload is a
 * handful of strings and scalars, and the client stays dependency-free.
 */
internal class BatchEncoder {
    // Not thread-safe, and deliberately so: only the sender thread encodes.
    private val timestamps = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US)
        .apply { timeZone = TimeZone.getTimeZone("UTC") }

    /** One flat object of scalars — what the batch context is, and what the key carries as text. */
    fun encodeObject(values: Map<String, Any>): String = buildString {
        append('{')
        var first = true
        for ((key, value) in values) {
            if (!first) append(',')
            first = false
            string(key)
            append(':')
            scalar(value)
        }
        append('}')
    }

    fun encode(key: BatchKey, events: List<Event>): String = buildString {
        append('{')
        field("installId", key.installId)

        val device = key.device
        optional("build", device.build)
        optional("platform", device.platform)
        optional("osVersion", device.osVersion)
        optional("deviceClass", device.deviceClass)
        optional("locale", device.locale)
        optional("country", device.country)
        optional("store", device.store)

        // Already cleaned and canonical: written through as-is, not re-encoded.
        if (key.context.length > 2) {
            append(",\"context\":")
            append(key.context)
        }

        append(",\"events\":[")
        events.forEachIndexed { index, event ->
            if (index > 0) append(',')
            append("{\"name\":")
            string(event.name)
            append(",\"ts\":")
            string(timestamps.format(Date(event.tsMillis)))
            event.tzMinutes?.let { append(','); string("tz"); append(':'); append(it) }
            if (event.props.isNotEmpty()) {
                append(",\"props\":{")
                var first = true
                for ((propKey, value) in event.props) {
                    if (!first) append(',')
                    first = false
                    string(propKey)
                    append(':')
                    scalar(value)
                }
                append('}')
            }
            append('}')
        }
        append("]}")
    }

    private fun StringBuilder.field(name: String, value: String) {
        string(name)
        append(':')
        string(value)
    }

    private fun StringBuilder.optional(name: String, value: Any?) {
        if (value == null) return
        append(',')
        string(name)
        append(':')
        scalar(value)
    }

    private fun StringBuilder.scalar(value: Any) {
        when (value) {
            is String -> string(value)
            is Boolean -> append(if (value) "true" else "false")
            is Double -> append(number(value))
            else -> append(number((value as Number).toDouble()))
        }
    }

    /** Whole values are written without a fractional part, so `moves` reads as 34 and not 34.0. */
    private fun number(value: Double): String =
        if (value == Math.floor(value) && !value.isInfinite() && Math.abs(value) < 1e15)
            value.toLong().toString()
        else
            value.toString()

    private fun StringBuilder.string(value: String) {
        append('"')
        for (c in value) {
            when {
                c == '"' -> append("\\\"")
                c == '\\' -> append("\\\\")
                c == '\n' -> append("\\n")
                c == '\r' -> append("\\r")
                c == '\t' -> append("\\t")
                c < ' ' -> append("\\u%04x".format(c.code))
                else -> append(c)
            }
        }
        append('"')
    }
}
