package com.trentbuckley.tether.tv

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.net.HttpURLConnection
import java.net.URL

/** Enrollment state, the cached channel block list, and the few HTTP calls the TV makes. */
object Store {
    class ApiException(val code: Int, message: String) : Exception(message)

    private const val FILE = "tether_tv"

    private fun sp(ctx: Context) = ctx.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    fun serverUrl(ctx: Context): String? = sp(ctx).getString("server_url", null)
    fun token(ctx: Context): String? = sp(ctx).getString("token", null)
    fun isEnrolled(ctx: Context) = token(ctx) != null

    fun saveEnrollment(ctx: Context, url: String, token: String) {
        sp(ctx).edit().putString("server_url", url).putString("token", token).apply()
    }

    /** Blocked channel names, already lowercased by the server. Cached so blocking works offline. */
    fun blocked(ctx: Context): Set<String> = sp(ctx).getStringSet("blocked", emptySet()) ?: emptySet()
    fun setBlocked(ctx: Context, names: Set<String>) = sp(ctx).edit().putStringSet("blocked", names).apply()

    /** Last metadata a media app published, for checking what fields it fills in. */
    fun lastMeta(ctx: Context): String = sp(ctx).getString("last_meta", "") ?: ""
    fun setLastMeta(ctx: Context, v: String) = sp(ctx).edit().putString("last_meta", v).apply()

    /** Parent bypass codes as "salt:hash" pairs (the server never sends the codes themselves). */
    fun bypassCodes(ctx: Context): Set<String> = sp(ctx).getStringSet("bypass_codes", emptySet()) ?: emptySet()
    fun setBypassCodes(ctx: Context, v: Set<String>) = sp(ctx).edit().putStringSet("bypass_codes", v).apply()

    /** Epoch millis until which blocking is off; 0 when not bypassed. */
    fun bypassUntil(ctx: Context): Long = sp(ctx).getLong("bypass_until", 0L)
    fun setBypassUntil(ctx: Context, t: Long) = sp(ctx).edit().putLong("bypass_until", t).apply()
    fun bypassActive(ctx: Context) = System.currentTimeMillis() < bypassUntil(ctx)

    fun sha256Hex(s: String): String =
        java.security.MessageDigest.getInstance("SHA-256").digest(s.toByteArray()).joinToString("") { "%02x".format(it) }

    /** Same normalisation the server applies to stored channel names. */
    fun key(name: String?): String =
        java.text.Normalizer.normalize(name ?: "", java.text.Normalizer.Form.NFKC)
            .trim().lowercase().replace(Regex("\\s+"), " ")

    private fun open(url: String, method: String): HttpURLConnection {
        val c = URL(url).openConnection() as HttpURLConnection
        c.requestMethod = method
        c.connectTimeout = 15_000
        c.readTimeout = 15_000
        return c
    }

    private fun read(c: HttpURLConnection): String {
        val s = if (c.responseCode in 200..299) c.inputStream else c.errorStream
        val out = ByteArrayOutputStream()
        s?.use { it.copyTo(out) }
        return String(out.toByteArray())
    }

    fun enroll(baseUrl: String, email: String, password: String, name: String): String {
        val c = open("$baseUrl/api/device/enroll", "POST")
        c.setRequestProperty("Content-Type", "application/json")
        c.doOutput = true
        c.outputStream.use {
            it.write(JSONObject().put("email", email).put("password", password).put("name", name).put("kind", "tv").toString().toByteArray())
        }
        val raw = read(c)
        if (c.responseCode != 200) throw ApiException(c.responseCode, raw)
        return JSONObject(raw).getString("token")
    }

    private fun authed(ctx: Context, path: String, method: String): HttpURLConnection {
        val base = serverUrl(ctx) ?: throw ApiException(0, "not_enrolled")
        val t = token(ctx) ?: throw ApiException(0, "not_enrolled")
        val c = open("$base$path", method)
        c.setRequestProperty("Authorization", "Bearer $t")
        return c
    }

    /** Pulls the channel block list. Throws when offline; callers keep the cached list. */
    fun fetchBlocked(ctx: Context): Set<String> {
        val c = authed(ctx, "/api/device/blocked-channels", "GET")
        val raw = read(c)
        if (c.responseCode !in 200..299) throw ApiException(c.responseCode, raw)
        val arr = JSONArray(raw)
        return (0 until arr.length()).map { key(arr.getString(it)) }.filter { it.isNotEmpty() }.toSet()
    }

    fun fetchBypass(ctx: Context): Set<String> {
        val c = authed(ctx, "/api/device/bypass", "GET")
        val raw = read(c)
        if (c.responseCode !in 200..299) throw ApiException(c.responseCode, raw)
        val arr = JSONArray(raw)
        return (0 until arr.length()).map { arr.getJSONObject(it) }
            .map { "${it.getString("salt")}:${it.getString("hash")}" }.toSet()
    }

    fun postWatch(ctx: Context, ts: Long, title: String, channel: String?, app: String, blocked: Boolean) {
        val c = authed(ctx, "/api/device/watch", "POST")
        c.setRequestProperty("Content-Type", "application/json")
        c.doOutput = true
        val body = JSONObject().put("ts", ts).put("title", title).put("channel", channel ?: JSONObject.NULL)
            .put("app_name", app).put("blocked", blocked)
        c.outputStream.use { it.write(body.toString().toByteArray()) }
        val raw = read(c)
        if (c.responseCode !in 200..299) throw ApiException(c.responseCode, raw)
    }

    /** Long-poll for commands from the portal; blocks up to ~30 s. */
    fun getCommands(ctx: Context): JSONArray {
        val c = authed(ctx, "/api/device/commands", "GET")
        c.readTimeout = 40_000
        val raw = read(c)
        if (c.responseCode !in 200..299) throw ApiException(c.responseCode, raw)
        return JSONArray(raw.ifBlank { "[]" })
    }

    fun commandDone(ctx: Context, id: Long, ok: Boolean) {
        val c = authed(ctx, "/api/device/commands/$id/done", "POST")
        c.setRequestProperty("Content-Type", "application/json")
        c.doOutput = true
        c.outputStream.use { it.write(JSONObject().put("ok", ok).toString().toByteArray()) }
        read(c)
    }

    fun postScreen(ctx: Context, jpeg: ByteArray) {
        val c = authed(ctx, "/api/device/screen", "POST")
        c.setRequestProperty("Content-Type", "image/jpeg")
        c.doOutput = true
        c.outputStream.use { it.write(jpeg) }
        read(c)
    }
}
