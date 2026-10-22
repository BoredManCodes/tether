package com.trentbuckley.tether.sync

import android.content.Context
import android.os.Handler
import android.os.Looper
import com.trentbuckley.tether.Prefs
import com.trentbuckley.tether.net.Api
import com.trentbuckley.tether.screen.ScreenStream
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap
import java.util.regex.Pattern
import kotlin.concurrent.thread

/**
 * Keeps a local copy of the parent's keyword list and watches two sources:
 *  - incoming notifications and SMS (check), which the server also alerts on
 *    when the message syncs later, and
 *  - text visible on screen or typed (checkScreen), which the phone reports
 *    straight to the server.
 * Either way a hit takes a screenshot right then and uploads it as media
 * (kind=screenshot) stamped with the hit time; the server attaches the one
 * nearest the alert's timestamp to the email.
 */
object KeywordWatch {

    // One shot covers hits within this window; the server matches within 30s.
    private const val COOLDOWN_MS = 20_000L
    // The same word in the same app only reports again after this long, so a
    // page that stays open or a chat that scrolls does not repeat the alert.
    private const val SCREEN_REPEAT_MS = 10 * 60_000L
    private val handler = Handler(Looper.getMainLooper())
    private val reported = ConcurrentHashMap<String, Long>()
    @Volatile private var lastShotAt = 0L

    /** Pulls the keyword list from the server. Called from the periodic sync tick. */
    fun refresh(ctx: Context) {
        try {
            val arr = JSONArray(Api.get(ctx, "/api/device/keywords"))
            val plain = mutableListOf<String>()
            val regexes = mutableListOf<String>()
            for (i in 0 until arr.length()) {
                // Older servers send bare strings.
                val o = arr.optJSONObject(i)
                if (o == null) plain.add(arr.getString(i))
                else if (o.optBoolean("regex")) regexes.add(o.getString("word"))
                else plain.add(o.getString("word"))
            }
            Prefs.setKeywords(ctx, plain, regexes)
        } catch (_: Exception) {
            // Offline or old server; keep the last known list.
        }
    }

    /** [ts] is the message time; [delayMs] lets a notification banner draw before the shot. */
    fun check(ctx: Context, text: String, ts: Long, delayMs: Long = 0L) {
        if (text.isBlank()) return
        val lower = text.lowercase()
        val hit = Prefs.keywords(ctx).any { lower.contains(it) } ||
            patterns(ctx).any { it.matcher(text).find() }
        if (hit) shoot(ctx.applicationContext, ts, delayMs)
    }

    /**
     * Text seen on screen in [pkg]. Matches must start at a word boundary so
     * "sex" does not fire on "Essex"; the end is left open so "sexual" still does.
     */
    fun checkScreen(ctx: Context, text: String, pkg: String) {
        if (text.isBlank()) return
        val lower = text.lowercase()
        val now = System.currentTimeMillis()
        val hits = mutableListOf<String>()
        var first = -1
        fun record(label: String, at: Int) {
            val key = "$pkg|$label"
            if (now - (reported[key] ?: 0L) < SCREEN_REPEAT_MS) return
            reported[key] = now
            hits.add(label)
            if (first < 0 || at < first) first = at
        }
        for (word in Prefs.keywords(ctx)) {
            val at = indexOfWord(lower, word)
            if (at >= 0) record(word, at)
        }
        for (p in patterns(ctx)) {
            val m = p.matcher(text)
            // Label with the text that matched, never the pattern itself.
            if (m.find()) record(m.group().lowercase(), m.start())
        }
        if (hits.isEmpty()) return

        val app = ctx.applicationContext
        shoot(app, now, 0L)
        val snippet = text.substring(maxOf(0, first - 80), minOf(text.length, first + 120)).replace(Regex("\\s+"), " ").trim()
        val body = JSONObject()
            .put("ts", now)
            .put("words", hits.joinToString(", "))
            .put("package", pkg)
            .put("app_name", AppLabelResolver.labelFor(app, pkg) ?: pkg)
            .put("snippet", snippet)
        thread {
            try { Api.post(app, "/api/device/screen-hit", body) } catch (_: Exception) {}
        }
    }

    // Compiled once per distinct regex set; a pattern the phone can't compile is skipped.
    @Volatile private var compiled: Pair<Set<String>, List<Pattern>> = emptySet<String>() to emptyList()

    private fun patterns(ctx: Context): List<Pattern> {
        val sources = Prefs.regexes(ctx)
        if (compiled.first != sources) {
            compiled = sources to sources.mapNotNull {
                try { Pattern.compile(it, Pattern.CASE_INSENSITIVE or Pattern.UNICODE_CASE) } catch (_: Exception) { null }
            }
        }
        return compiled.second
    }

    private fun indexOfWord(lower: String, word: String): Int {
        var from = 0
        while (true) {
            val at = lower.indexOf(word, from)
            if (at < 0) return -1
            if (at == 0 || !lower[at - 1].isLetterOrDigit()) return at
            from = at + 1
        }
    }

    private fun shoot(app: Context, ts: Long, delayMs: Long) {
        val now = System.currentTimeMillis()
        if (now - lastShotAt < COOLDOWN_MS) return
        lastShotAt = now
        handler.postDelayed({ ScreenStream.captureToMedia(app, ts) }, delayMs)
    }
}
