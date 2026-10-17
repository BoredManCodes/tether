package com.trentbuckley.tether.sync

import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentLinkedQueue

/**
 * DNS lookups seen by TetherVpnService, waiting for the next sync tick.
 * The same domain from the same app inside DEDUPE_MS is dropped; a page
 * load fires dozens of identical lookups and the dashboard only needs
 * "this site, this app, around this time".
 */
object DnsLog {

    data class Entry(val ts: Long, val domain: String, val pkg: String?)

    private const val MAX_QUEUED = 3000
    private const val DEDUPE_MS = 60_000L
    private val queue = ConcurrentLinkedQueue<Entry>()
    private val lastSeen = ConcurrentHashMap<String, Long>()

    fun add(domain: String, pkg: String?) {
        val now = System.currentTimeMillis()
        val key = "$domain|$pkg"
        val prev = lastSeen[key]
        if (prev != null && now - prev < DEDUPE_MS) return
        lastSeen[key] = now
        if (lastSeen.size > 5000) lastSeen.entries.removeIf { now - it.value > DEDUPE_MS }
        queue.add(Entry(now, domain, pkg))
        while (queue.size > MAX_QUEUED) queue.poll()
    }

    fun drain(): List<Entry> {
        val out = mutableListOf<Entry>()
        while (true) {
            out.add(queue.poll() ?: break)
        }
        return out
    }
}
