package com.trentbuckley.tether.sync

import android.app.*
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.media.AudioAttributes
import android.media.MediaPlayer
import android.net.Uri
import android.os.*
import androidx.core.app.NotificationCompat
import com.trentbuckley.tether.Prefs
import com.trentbuckley.tether.admin.Hardening
import com.trentbuckley.tether.admin.LockManager
import com.trentbuckley.tether.net.Api
import com.trentbuckley.tether.screen.ScreenStream
import org.json.JSONObject
import kotlin.concurrent.thread

/**
 * Long-running foreground service that:
 *  - periodically pushes location/battery/sms/calls/notifications/usage,
 *  - long-polls for commands and executes them,
 *  - exposes a way for other components (SmsReceiver) to force an
 *    immediate sync instead of waiting for the next tick.
 *
 * Needs to be a foreground service (location type) so Android doesn't
 * kill it in the background; the notification is low priority and
 * worded plainly since the kid will see it.
 */
class TetherSyncService : Service() {

    private val handler = Handler(Looper.getMainLooper())
    private var running = false
    private var pollThread: Thread? = null

    override fun onCreate() {
        super.onCreate()
        startForeground(NOTIF_ID, buildNotification())
        running = true
        startPeriodicSync()
        startCommandPoll()
        registerPackageWatcher()
    }

    // Reinstalling a block + uninstall app should undo itself within seconds, not at the next 5 minute tick.
    private val packageWatcher = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            thread { AppRuleManager.checkAndApply(this@TetherSyncService) }
        }
    }

    private fun registerPackageWatcher() {
        val filter = IntentFilter(Intent.ACTION_PACKAGE_ADDED).apply { addDataScheme("package") }
        registerReceiver(packageWatcher, filter)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_SYNC_NOW) syncOnce()
        return START_STICKY
    }

    override fun onDestroy() {
        running = false
        try { unregisterReceiver(packageWatcher) } catch (_: Exception) {}
        pollThread?.interrupt()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun buildNotification(): Notification {
        val channelId = "tether_sync"
        val mgr = getSystemService(NotificationManager::class.java)
        if (mgr.getNotificationChannel(channelId) == null) {
            mgr.createNotificationChannel(
                NotificationChannel(channelId, "Tether", NotificationManager.IMPORTANCE_MIN)
            )
        }
        return NotificationCompat.Builder(this, channelId)
            .setContentTitle("Tether is active")
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setPriority(NotificationCompat.PRIORITY_MIN)
            .setOngoing(true)
            .build()
    }

    // ---------- periodic data sync ----------

    private fun startPeriodicSync() {
        val runnable = object : Runnable {
            override fun run() {
                if (!running) return
                syncOnce()
                thread { ScheduleManager.checkAndApply(this@TetherSyncService) }
                thread { AppRuleManager.checkAndApply(this@TetherSyncService) }
                thread { Hardening.apply(this@TetherSyncService) }
                handler.postDelayed(this, SYNC_INTERVAL_MS)
            }
        }
        handler.post(runnable)
    }

    private fun syncOnce() {
        thread {
            try {
                val body = DataCollector.collect(this)
                Api.post(this, "/api/device/sync", body)
            } catch (_: Exception) {
                // No connectivity or server down; next tick tries again.
            }
        }
    }

    // ---------- command long-poll ----------

    private fun startCommandPoll() {
        pollThread = thread {
            while (running) {
                try {
                    val result = Api.getCommands(this)
                    val items = result.getJSONArray("items")
                    for (i in 0 until items.length()) {
                        val cmd = items.getJSONObject(i)
                        runCommand(cmd)
                    }
                } catch (_: Exception) {
                    Thread.sleep(5_000)
                }
            }
        }
    }

    private fun runCommand(cmd: JSONObject) {
        val id = cmd.getLong("id")
        val type = cmd.getString("type")
        var ok = true
        try {
            when (type) {
                "LOCATE" -> syncOnce()
                "SCAN_MEDIA" -> MediaUploader.scanAndUpload(this)
                "LOCK" -> LockManager.lock(this)
                "UNLOCK" -> LockManager.unlock(this)
                "RING" -> ring()
                "SCREEN_START" -> ScreenStream.start(this)
                "SCREEN_STOP" -> ScreenStream.stop()
                "SCREENSHOT" -> ScreenStream.captureOnce(this)
                else -> ok = false
            }
        } catch (_: Exception) {
            ok = false
        }
        try {
            Api.post(this, "/api/device/commands/$id/done", JSONObject().put("ok", ok))
        } catch (_: Exception) {
        }
    }

    private fun ring() {
        val uri = Uri.parse("content://settings/system/notification_sound")
        val player = MediaPlayer()
        player.setAudioAttributes(
            AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ALARM).build()
        )
        player.setDataSource(this, uri)
        player.isLooping = false
        player.setOnCompletionListener { it.release() }
        player.prepare()
        player.start()
    }

    companion object {
        private const val NOTIF_ID = 1
        private const val SYNC_INTERVAL_MS = 5 * 60 * 1000L
        private const val ACTION_SYNC_NOW = "com.trentbuckley.tether.SYNC_NOW"

        // Android 12+ throws if the process was started in the background
        // (SMS receiver, accessibility bind) and we try to start a foreground
        // service from Application.onCreate. That crashed the whole app.
        fun start(ctx: Context) {
            val intent = Intent(ctx, TetherSyncService::class.java)
            try {
                ctx.startForegroundService(intent)
            } catch (_: Exception) {
                // Not allowed right now; the next foreground start or boot retries.
            }
        }

        fun requestImmediateSync(ctx: Context) {
            val intent = Intent(ctx, TetherSyncService::class.java).apply { action = ACTION_SYNC_NOW }
            try {
                ctx.startForegroundService(intent)
            } catch (_: Exception) {
            }
        }
    }
}
