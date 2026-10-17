package com.trentbuckley.tether.notif

import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import java.util.concurrent.ConcurrentLinkedQueue

/**
 * Captures notification postings for the sync batch. Device Owner can't
 * flip this service on by itself; it has to be enabled once over adb
 * during provisioning (settings put secure enabled_notification_listeners).
 *
 * Keeps a bounded in-memory queue; DataCollector drains it on each sync
 * tick, so nothing is persisted here beyond process lifetime.
 */
class TetherNotificationListener : NotificationListenerService() {

    data class Entry(val ts: Long, val pkg: String, val title: String, val text: String)

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        val extras = sbn.notification.extras
        val title = extras.getCharSequence("android.title")?.toString() ?: ""
        val text = extras.getCharSequence("android.text")?.toString() ?: ""
        if (title.isBlank() && text.isBlank()) return
        queue.add(Entry(sbn.postTime, sbn.packageName, title, text))
        while (queue.size > MAX_QUEUED) queue.poll()
    }

    companion object {
        private const val MAX_QUEUED = 500
        private val queue = ConcurrentLinkedQueue<Entry>()

        fun drain(): List<Entry> {
            val out = mutableListOf<Entry>()
            while (true) {
                out.add(queue.poll() ?: break)
            }
            return out
        }
    }
}
