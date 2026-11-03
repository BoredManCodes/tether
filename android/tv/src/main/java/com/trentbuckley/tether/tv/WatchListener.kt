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
 * Media apps (SmartTube, YouTube, Netflix, ...) publish what is playing to a
 * MediaSession. Reading it needs notification-listener access, so this service
 * is the way in; it never looks at notifications. Each new item is reported to
 * the server, and when its channel (the session's artist) is on the block list
 * the TV pauses and presses Back until it stops. Apps that leave the artist
 * empty are logged by title only and cannot be blocked.
 */
class WatchListener : NotificationListenerService() {

    private val worker = HandlerThread("tether-tv").apply { start() }
    private val handler by lazy { Handler(worker.looper) }
    private lateinit var msm: MediaSessionManager
    private val callbacks = mutableMapOf<MediaController, MediaController.Callback>()
    @Volatile private var blocked: Set<String> = emptySet()
    private val pending = ArrayDeque<Runnable>() // uploads that failed, retried on the next refresh
    private val lastReported = mutableMapOf<String, String>() // package -> last "title|channel"

    private val sessionsChanged = MediaSessionManager.OnActiveSessionsChangedListener { attach(it.orEmpty()) }

    override fun onListenerConnected() {
        super.onListenerConnected()
        blocked = Store.blocked(this)
        msm = getSystemService(MEDIA_SESSION_SERVICE) as MediaSessionManager
        connectSessions()
        handler.post(refreshLoop)
        startCommandPoll()
    }

    @Volatile private var polling = false

    // After a reinstall Android sometimes refuses media access for a moment; retry until it sticks.
    private fun connectSessions() {
        val me = ComponentName(this, WatchListener::class.java)
        try {
            msm.addOnActiveSessionsChangedListener(sessionsChanged, me, handler)
            attach(msm.getActiveSessions(me))
            mediaOk = true
        } catch (e: SecurityException) {
            mediaOk = false
            Log.w(TAG, "no session access, retrying", e)
            handler.postDelayed({ connectSessions() }, 10_000L)
        }
    }

    private fun startCommandPoll() {
        if (polling) return
        polling = true
        Thread {
            while (polling) {
                try {
                    if (!Store.isEnrolled(this)) { Thread.sleep(5_000); continue }
                    val items = Store.getCommands(this)
                    for (i in 0 until items.length()) {
                        val cmd = items.getJSONObject(i)
                        val ok = when (cmd.getString("type")) {
                            "SCREEN_START" -> { BackService.startStream(applicationContext); true }
                            "SCREEN_STOP" -> { BackService.stopStream(); true }
                            "SCREENSHOT" -> BackService.grab(applicationContext)
                            else -> false
                        }
                        try { Store.commandDone(this, cmd.getLong("id"), ok) } catch (_: Exception) {}
                    }
                } catch (_: Exception) {
                    try { Thread.sleep(5_000) } catch (_: InterruptedException) {}
                }
            }
        }.apply { isDaemon = true; start() }
    }

    override fun onListenerDisconnected() {
        mediaOk = false
        polling = false
        BackService.stopStream()
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
                    try { Store.setBypassCodes(this@WatchListener, Store.fetchBypass(this@WatchListener)) } catch (_: Exception) {}
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

    private fun appName(pkg: String): String = try {
        packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString()
    } catch (_: Exception) { pkg }

    private fun attach(list: List<MediaController>) {
        val wanted = list.filter { it.packageName != packageName }
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

    private data class Video(val title: String, val channel: String?, val app: String)

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
        return Video(title, channel, appName(c.packageName))
    }

    private fun active(c: MediaController): Boolean = when (c.playbackState?.state) {
        PlaybackState.STATE_PLAYING, PlaybackState.STATE_BUFFERING,
        PlaybackState.STATE_PAUSED, PlaybackState.STATE_CONNECTING -> true
        else -> false
    }

    private fun evaluate(c: MediaController) {
        if (!active(c)) return
        val v = read(c) ?: return
        // During a parent bypass nothing is blocked, and nothing is logged as blocked.
        val isBlocked = !Store.bypassActive(this) && v.channel != null && Store.key(v.channel) in blocked
        val id = "${v.title}|${v.channel}"
        val first = lastReported[c.packageName] != id
        lastReported[c.packageName] = id
        if (first || isBlocked) report(v, isBlocked)
        if (isBlocked) {
            BackService.showBlocked(v.channel)
            enforce(c, id, attempt = 0)
        }
    }

    private fun report(v: Video, isBlocked: Boolean) {
        val ts = System.currentTimeMillis()
        val task = object : Runnable {
            override fun run() {
                try {
                    Store.postWatch(this@WatchListener, ts, v.title, v.channel, v.app, isBlocked)
                } catch (_: Exception) {
                    if (pending.size < 200) pending.addLast(this)
                }
            }
        }
        if (Store.isEnrolled(this)) task.run()
    }

    // Back from the player returns to the grid; if the video is still there a second later, press again.
    private fun enforce(c: MediaController, id: String, attempt: Int) {
        if (attempt >= MAX_BACKS || Store.bypassActive(this)) return
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
        /** True once the listener has working access to media sessions. */
        @Volatile var mediaOk = false
        private const val TAG = "TetherTv"
        private const val SETTLE_MS = 1_200L
        private const val RETRY_MS = 1_500L
        private const val REFRESH_MS = 30_000L
        private const val MAX_BACKS = 5
    }
}
