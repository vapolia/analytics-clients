package com.vapolia.analytics

import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

internal sealed interface SendResult {
    object Ok : SendResult

    /** Worth sending again: a network error, a 5xx, or a 429 with its `Retry-After`. */
    data class Retry(val afterMs: Long, val reason: String) : SendResult

    /** A retry cannot fix it: unknown source, malformed payload. */
    data class Permanent(val reason: String) : SendResult
}

/** One request to `POST {endpoint}/{source}`. Built on HttpURLConnection so the client has no dependency. */
internal class Transport(
    private val url: String,
    private val connectTimeoutMs: Int,
    private val readTimeoutMs: Int,
    private val token: String? = null,
) {
    fun post(body: ByteArray): SendResult {
        val connection = try {
            (URL(url).openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                connectTimeout = connectTimeoutMs
                readTimeout = readTimeoutMs
                doOutput = true
                useCaches = false
                setFixedLengthStreamingMode(body.size)
                setRequestProperty("Content-Type", "application/json")
                if (!token.isNullOrBlank())
                    setRequestProperty("Authorization", "Bearer $token")
            }
        } catch (e: Exception) {
            return SendResult.Permanent("cannot open $url: $e")
        }

        return try {
            connection.outputStream.use { it.write(body) }

            when (val status = connection.responseCode) {
                in 200..299 -> SendResult.Ok

                429 -> SendResult.Retry(retryAfterMs(connection), "rate limited (429)")

                // 404 means this source is not configured on that collector, and 400 that the payload
                // is not what it accepts. Neither improves by being sent again.
                404 -> SendResult.Permanent("unknown source (404)")
                in 400..499 -> SendResult.Permanent("refused with $status")

                else -> SendResult.Retry(0, "collector answered $status")
            }
        } catch (e: IOException) {
            SendResult.Retry(0, "network error: $e")
        } finally {
            // Drain, so the connection can be pooled rather than torn down.
            runCatching { connection.errorStream?.use { it.readBytes() } }
            runCatching { connection.inputStream?.use { it.readBytes() } }
            connection.disconnect()
        }
    }

    private fun retryAfterMs(connection: HttpURLConnection): Long {
        val seconds = connection.getHeaderField("Retry-After")?.trim()?.toLongOrNull() ?: return 0
        return if (seconds < 0) 0 else seconds * 1000
    }
}
