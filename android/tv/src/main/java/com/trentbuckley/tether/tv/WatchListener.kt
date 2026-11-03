package com.trentbuckley.tether.tv

import android.content.ComponentName
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.HandlerThread
import android.service.notification.NotificationListenerService
import android.util.Log

/**
 * SmartTube publishes the playing video to a MediaSession. Reading it needs
 * notification-listener access, so this service is the way in; it never looks
 * at notifications. Each new video is reported to the server, and when its
 * channel is on the block list the TV pauses and presses Back until it stops.
 */
class WatchListener : NotificationListenerService() {

    private val worker = HandlerThread("tether-tv").apply { start() }
    private val handler by lazy { Handler(worker.looper) }
    private lateinit var msm: MediaSessionManager
    private val callbacks = mutableMapOf<MediaController, MediaController.Callback>()
    @Volatile private var blocked: Set<String> = emptySet()
    private val pending = ArrayDeque<Runnable>() // uploads that failed, retried on the next refresh
    private var lastReported = ""

    private val sessionsChanged = MediaSessionManager.OnActiveSessionsChangedListener { attach(it.orEmpty()) }

    override fun onListenerConnected() {
        super.onListenerConnected()
        blocked = Store.blocked(this)
        msm = getSystemService(MEDIA_SESSION_SERVICE) as MediaSessionManager
        val me = ComponentName(this, WatchListener::class.java)
        try {
            msm.addOnActiveSessionsChangedListener(sessionsChanged, me, handler)
            attach(msm.getActiveSessions(me))
        } catch (e: SecurityException) {
            Log.w(TAG, "no session access", e)
        }
        handler.post(refreshLoop)
    }

    override fun onListenerDisconnected() {
        handler.removeCallbacksAndMessages(null)
        try { msm.removeOnActiveSessionsChangedListener(sessionsChanged) } catch (_: Exception) {}
        detachAll()
        requestRebind(ComponentName(this, WatchListener::class.java))
        super.onListenerDisconnected()
    }

    override fun onDestroy() {
        worker.quitSafely()
        super.onDestroy()
    }

    private val refreshLoop = object : Runnable {
        override fun run() {
            if (Store.isEnrolled(this@WatchListener)) {
                try {
                    val fresh = Store.fetchBlocked(this@WatchListener)
                    Store.setBlocked(this@WatchListener, fresh)
                    blocked = fresh
                    // A channel blocked while its video plays takes effect now, not at the next video.
                    callbacks.keys.forEach { evaluate(it) }
                } catch (_: Exception) {
                    // Offline or server down; keep the cached list.
                }
                repeat(pending.size) { pending.removeFirstOrNull()?.run() }
            }
            handler.postDelayed(this, REFRESH_MS)
        }
    }

    private fun isSmartTube(pkg: String) = SMARTTUBE_PREFIXES.any { pkg.startsWith(it) }

    private fun attach(list: List<MediaController>) {
        val wanted = list.filter { isSmartTube(it.packageName) }
        for (c in callbacks.keys.toList()) {
            if (wanted.none { it.sessionToken == c.sessionToken }) {
                callbacks.remove(c)?.let { c.unregisterCallback(it) }
            }
        }
        for (c in wanted) {
            if (callbacks.keys.any { it.sessionToken == c.sessionToken }) continue
            val cb = object : MediaController.Callback() {
                override fun onMetadataChanged(metadata: MediaMetadata?) = schedule(c)
                override fun onPlaybackStateChanged(state: PlaybackState?) = schedule(c)
            }
            c.registerCallback(cb, handler)
            callbacks[c] = cb
            schedule(c)
        }
    }

    private fun detachAll() {
        callbacks.forEach { (c, cb) -> try { c.unregisterCallback(cb) } catch (_: Exception) {} }
        callbacks.clear()
    }

    // Title and artist can update a moment apart; wait for them to settle so the
    // title is never paired with the previous video's channel.
    private fun schedule(c: MediaController) {
        handler.removeCallbacks(settle)
        handler.postDelayed(settle, SETTLE_MS)
    }

    private val settle = Runnable { callbacks.keys.forEach { evaluate(it) } }

    private data class Video(val title: String, val channel: String?)

    private fun read(c: MediaController): Video? {
        val md = c.metadata ?: return null
        val sb = StringBuilder()
        for (k in md.keySet()) {
            val v = try { md.getString(k) } catch (_: Exception) { null }
            if (v != null) sb.append(k).append('=').append(v.take(120)).append('\n')
        }
        Store.setLastMeta(this, sb.toString())

        val title = (md.getString(MediaMetadata.METADATA_KEY_TITLE)
            ?: md.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE))?.trim()
        if (title.isNullOrEmpty()) return null
        val raw = md.getString(MediaMetadata.METADATA_KEY_ARTIST)
            ?: md.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST)
            ?: md.getString(MediaMetadata.METADATA_KEY_AUTHOR)
            ?: md.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE)
        // Some builds append " • 1.2M views"; the channel is the first part.
        val channel = raw?.split(" • ")?.first()?.trim()?.ifEmpty { null }
        return Video(title, channel)
    }

    private fun active(c: MediaController): Boolean = when (c.playbackState?.state) {
        PlaybackState.STATE_PLAYING, PlaybackState.STATE_BUFFERING,
        PlaybackState.STATE_PAUSED, PlaybackState.STATE_CONNECTING -> true
        else -> false
    }

    private fun evaluate(c: MediaController) {
        if (!active(c)) return
        val v = read(c) ?: return
        val isBlocked = v.channel != null && Store.key(v.channel) in blocked
        val id = "${v.title}|${v.channel}"
        if (id != lastReported || isBlocked) {
            val first = id != lastReported
            lastReported = id
            if (first || isBlocked) report(v, isBlocked)
        }
        if (isBlocked) enforce(c, id, attempt = 0)
    }

    private fun report(v: Video, isBlocked: Boolean) {
        val ts = System.currentTimeMillis()
        val task = object : Runnable {
            override fun run() {
                try {
                    Store.postWatch(this@WatchListener, ts, v.title, v.channel, "SmartTube", isBlocked)
                } catch (_: Exception) {
                    if (pending.size < 200) pending.addLast(this)
                }
            }
        }
        if (Store.isEnrolled(this)) task.run()
    }

    // Back from the player returns to the grid; if the video is still there a second later, press again.
    private fun enforce(c: MediaController, id: String, attempt: Int) {
        if (attempt >= MAX_BACKS) return
        try { c.transportControls.pause() } catch (_: Exception) {}
        if (!BackService.back()) Log.w(TAG, "accessibility service off; cannot press Back")
        handler.postDelayed({
            if (active(c)) {
                val v = read(c)
                if (v != null && "${v.title}|${v.channel}" == id) enforce(c, id, attempt + 1)
            }
        }, RETRY_MS)
    }

    companion object {
        private const val TAG = "TetherTv"
        private const val SETTLE_MS = 1_200L
        private const val RETRY_MS = 1_500L
        private const val REFRESH_MS = 30_000L
        private const val MAX_BACKS = 5
        // org.smarttube.* is current; the other two are older release names.
        val SMARTTUBE_PREFIXES = listOf("org.smarttube", "com.teamsmart.videomanager", "com.liskovsoft.smarttubetv")
    }
}
