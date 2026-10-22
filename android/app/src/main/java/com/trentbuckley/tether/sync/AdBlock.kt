package com.trentbuckley.tether.sync

import android.content.Context
import com.trentbuckley.tether.Prefs
import com.trentbuckley.tether.net.Api
import org.json.JSONObject
import java.io.File

/**
 * Domain blocklist for the DNS filter in TetherVpnService. The server builds
 * one list per device (its enabled categories plus custom blocks) and a
 * version string that changes whenever that list would. This keeps a copy in
 * files/ so lookups are answered locally with no network round trip. A name
 * is blocked when it or any parent domain is on the list.
 *
 * Domains are stored as sorted 64-bit hashes rather than strings: a list with
 * the adult category runs past a million names, and this keeps it near 8 MB.
 */
object AdBlock {

    private const val FILE = "blocklist.txt"

    @Volatile private var hashes = LongArray(0)
    @Volatile private var loadedAt = -1L

    fun isBlocked(ctx: Context, name: String): Boolean {
        ensureLoaded(ctx)
        val set = hashes
        if (set.isEmpty()) return false
        var start = 0
        while (start < name.length) {
            if (java.util.Arrays.binarySearch(set, fnv(name, start)) >= 0) return true
            val dot = name.indexOf('.', start)
            if (dot < 0) break
            start = dot + 1
        }
        return false
    }

    /** Re-downloads the list when the server's version differs from ours. Called from the periodic sync tick. */
    fun refresh(ctx: Context) {
        try {
            val version = JSONObject(Api.get(ctx, "/api/device/adblock")).optString("version")
            val f = File(ctx.filesDir, FILE)
            if (version.isEmpty() || (version == Prefs.blocklistVersion(ctx) && f.exists())) return
            val tmp = File(ctx.filesDir, "$FILE.tmp")
            val got = Api.download(ctx, "/api/device/blocklist", tmp, "X-Blocklist-Version") ?: version
            tmp.renameTo(f)
            Prefs.setBlocklistVersion(ctx, got)
        } catch (_: Exception) {
            // Offline or old server; keep the last known list.
        }
    }

    @Synchronized
    private fun ensureLoaded(ctx: Context) {
        val f = File(ctx.filesDir, FILE)
        if (!f.exists() || f.lastModified() == loadedAt) return
        try {
            var arr = LongArray(1 shl 16)
            var n = 0
            f.forEachLine { line ->
                val d = line.trim().lowercase()
                if (d.isNotEmpty()) {
                    if (n == arr.size) arr = arr.copyOf(n * 2)
                    arr[n++] = fnv(d, 0)
                }
            }
            val out = arr.copyOf(n)
            out.sort()
            hashes = out
            loadedAt = f.lastModified()
        } catch (_: Exception) {}
    }

    /** FNV-1a 64-bit over s[from..]. */
    private fun fnv(s: String, from: Int): Long {
        var h = -0x340d631b7bdddcdbL
        for (i in from until s.length) {
            h = (h xor s[i].code.toLong()) * 0x100000001b3L
        }
        return h
    }
}
