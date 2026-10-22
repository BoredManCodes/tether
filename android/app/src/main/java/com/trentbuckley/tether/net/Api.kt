package com.trentbuckley.tether.net

import android.content.Context
import com.trentbuckley.tether.Prefs
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.TimeUnit

/**
 * Tiny HTTP client for talking to the Tether backend. No OkHttp/Retrofit;
 * this app makes maybe a dozen request shapes total and HttpURLConnection
 * covers all of them without extra dependencies.
 */
object Api {

    class ApiException(val code: Int, message: String) : Exception(message)

    private fun open(url: String, method: String, timeoutMs: Int = 15_000): HttpURLConnection {
        val conn = URL(url).openConnection() as HttpURLConnection
        conn.requestMethod = method
        conn.connectTimeout = timeoutMs
        conn.readTimeout = timeoutMs
        return conn
    }

    private fun readAll(conn: HttpURLConnection): ByteArray {
        val stream = if (conn.responseCode in 200..299) conn.inputStream else conn.errorStream
        val out = ByteArrayOutputStream()
        stream?.use { it.copyTo(out) }
        return out.toByteArray()
    }

    fun enroll(baseUrl: String, email: String, password: String, deviceName: String): Pair<Long, String> {
        val conn = open("$baseUrl/api/device/enroll", "POST")
        conn.setRequestProperty("Content-Type", "application/json")
        conn.doOutput = true
        val body = JSONObject().put("email", email).put("password", password).put("name", deviceName)
        conn.outputStream.use { it.write(body.toString().toByteArray()) }
        val raw = readAll(conn)
        if (conn.responseCode != 200) throw ApiException(conn.responseCode, String(raw))
        val json = JSONObject(String(raw))
        return json.getLong("device_id") to json.getString("token")
    }

    /** Authenticated JSON POST, returns the parsed response body (or empty object on empty body). */
    fun post(ctx: Context, path: String, body: JSONObject, timeoutMs: Int = 15_000): JSONObject {
        val base = Prefs.serverUrl(ctx) ?: throw ApiException(0, "not_enrolled")
        val token = Prefs.deviceToken(ctx) ?: throw ApiException(0, "not_enrolled")
        val conn = open("$base$path", "POST", timeoutMs)
        conn.setRequestProperty("Authorization", "Bearer $token")
        conn.setRequestProperty("Content-Type", "application/json")
        conn.doOutput = true
        conn.outputStream.use { it.write(body.toString().toByteArray()) }
        val raw = readAll(conn)
        if (conn.responseCode !in 200..299) throw ApiException(conn.responseCode, String(raw))
        val text = String(raw).ifBlank { "{}" }
        return JSONObject(text)
    }

    /** Authenticated POST with a raw binary body (used for screen frames). */
    fun postBinary(ctx: Context, path: String, contentType: String, data: ByteArray) {
        val base = Prefs.serverUrl(ctx) ?: throw ApiException(0, "not_enrolled")
        val token = Prefs.deviceToken(ctx) ?: throw ApiException(0, "not_enrolled")
        val conn = open("$base$path", "POST")
        conn.setRequestProperty("Authorization", "Bearer $token")
        conn.setRequestProperty("Content-Type", contentType)
        conn.doOutput = true
        conn.outputStream.use { it.write(data) }
        readAll(conn)
        if (conn.responseCode !in 200..299) throw ApiException(conn.responseCode, "screen_post_failed")
    }

    /** Long-poll for pending commands. Blocks the calling thread up to ~30s. */
    fun getCommands(ctx: Context): JSONObject {
        val base = Prefs.serverUrl(ctx) ?: throw ApiException(0, "not_enrolled")
        val token = Prefs.deviceToken(ctx) ?: throw ApiException(0, "not_enrolled")
        val conn = open("$base/api/device/commands", "GET", TimeUnit.SECONDS.toMillis(35).toInt())
        conn.setRequestProperty("Authorization", "Bearer $token")
        val raw = readAll(conn)
        if (conn.responseCode !in 200..299) throw ApiException(conn.responseCode, String(raw))
        return JSONObject().put("items", org.json.JSONArray(String(raw).ifBlank { "[]" }))
    }

    fun get(ctx: Context, path: String): String {
        val base = Prefs.serverUrl(ctx) ?: throw ApiException(0, "not_enrolled")
        val token = Prefs.deviceToken(ctx) ?: throw ApiException(0, "not_enrolled")
        val conn = open("$base$path", "GET")
        conn.setRequestProperty("Authorization", "Bearer $token")
        val raw = readAll(conn)
        if (conn.responseCode !in 200..299) throw ApiException(conn.responseCode, String(raw))
        return String(raw)
    }

    /** Streams a large GET straight to [dest] and returns the response header [headerName], if any. */
    fun download(ctx: Context, path: String, dest: java.io.File, headerName: String): String? {
        val base = Prefs.serverUrl(ctx) ?: throw ApiException(0, "not_enrolled")
        val token = Prefs.deviceToken(ctx) ?: throw ApiException(0, "not_enrolled")
        val conn = open("$base$path", "GET", timeoutMs = 120_000)
        conn.setRequestProperty("Authorization", "Bearer $token")
        if (conn.responseCode !in 200..299) throw ApiException(conn.responseCode, String(readAll(conn)))
        conn.inputStream.use { input -> dest.outputStream().use { input.copyTo(it) } }
        return conn.getHeaderField(headerName)
    }
}
