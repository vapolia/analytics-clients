package com.vapolia.analytics

// Limits mirrored from the collector's EventSanitizer, which applies them again on arrival.
internal const val MAX_EVENTS_PER_BATCH = 100
internal const val MAX_PROPS_PER_EVENT = 12
internal const val MAX_CONTEXT_KEYS = 12
internal const val MAX_VALUE_LENGTH = 64
internal const val MAX_BUILD_LENGTH = 24
internal const val MAX_OS_VERSION_LENGTH = 24
internal const val MAX_LOCALE_LENGTH = 12

/** How far back the collector accepts a timestamp. Older events are dropped instead of sent. */
internal const val MAX_EVENT_AGE_MS = 7L * 24 * 60 * 60 * 1000

internal object Clean {
    val PLATFORMS = setOf("android", "ios", "maccatalyst", "windows", "web")
    val DEVICE_CLASSES = setOf("phone", "tablet", "desktop", "other")
    val STORES = setOf("google", "apple", "other")

    /**
     * Trims, caps the length, strips control characters, and turns blank into null. The control pass
     * is not cosmetic: Postgres rejects U+0000 in `text` and `jsonb`.
     */
    fun text(value: String?, maxLength: Int): String? {
        val trimmed = value?.trim().orEmpty()
        if (trimmed.isEmpty())
            return null

        val capped = if (trimmed.length > maxLength) trimmed.substring(0, maxLength) else trimmed
        val cleaned = capped.filter { !it.isISOControl() }.trim()
        return cleaned.ifEmpty { null }
    }

    /**
     * Exactly two ASCII letters, or nothing: truncating would turn "GERMANY" into "GE", which is
     * Georgia.
     */
    fun country(value: String?): String? {
        val trimmed = value?.trim() ?: return null
        if (trimmed.length != 2 || !trimmed.all { it in 'a'..'z' || it in 'A'..'Z' })
            return null
        return trimmed.uppercase()
    }

    fun pick(value: String?, allowed: Set<String>): String? {
        val normalized = value?.trim()?.lowercase() ?: return null
        return if (normalized in allowed) normalized else null
    }

    /** The canonical UUID form only, and never the nil UUID. */
    fun installId(value: String?): String? {
        val id = value?.trim()?.lowercase() ?: return null
        if (id.length != 36)
            return null

        var allZero = true
        id.forEachIndexed { index, c ->
            when (index) {
                8, 13, 18, 23 -> if (c != '-') return null
                else -> {
                    if (c !in '0'..'9' && c !in 'a'..'f') return null
                    if (c != '0') allZero = false
                }
            }
        }

        return if (allZero) null else id
    }

    /** Scalars only, capped in count and in length. */
    fun props(props: Map<String, Any?>?, maxKeys: Int = MAX_PROPS_PER_EVENT): Map<String, Any> {
        if (props.isNullOrEmpty())
            return emptyMap()

        val kept = LinkedHashMap<String, Any>(minOf(props.size, maxKeys))
        // Sorted, so what survives the cap does not depend on the map order and identical contexts
        // serialise identically.
        for (key in props.keys.sorted()) {
            if (kept.size == maxKeys)
                break

            when (val value = props[key]) {
                is String -> text(value, MAX_VALUE_LENGTH)?.let { kept[key] = it }
                is Boolean -> kept[key] = value
                // NaN and the infinities are not representable in jsonb.
                is Number -> value.toDouble().takeIf { it.isFinite() }?.let { kept[key] = it }
            }
        }

        return kept
    }
}

/**
 * The batch context as canonical JSON: cleaned like an event's props, sorted, and written once so the
 * key that groups batches is a plain string comparison.
 */
internal fun encodeContext(context: Map<String, Any?>?, encoder: BatchEncoder): String {
    val kept = Clean.props(context, MAX_CONTEXT_KEYS)
    return if (kept.isEmpty()) "{}" else encoder.encodeObject(kept)
}
