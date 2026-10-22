package com.trentbuckley.tether.admin

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.MediaPlayer
import android.media.RingtoneManager
import android.net.Uri
import android.os.Handler
import android.os.Looper

/**
 * Find-my-phone ring: the default ringtone on the alarm stream at max
 * volume for 10 seconds, so it sounds even when the phone is on silent.
 * The alarm volume is put back afterwards.
 */
object Ringer {

    private const val RING_MS = 10_000L
    private val handler = Handler(Looper.getMainLooper())
    private var player: MediaPlayer? = null
    private var savedVolume = -1

    fun ring(ctx: Context) {
        val app = ctx.applicationContext
        handler.post {
            stop(app)
            val am = app.getSystemService(AudioManager::class.java)
            savedVolume = am.getStreamVolume(AudioManager.STREAM_ALARM)
            am.setStreamVolume(AudioManager.STREAM_ALARM, am.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0)

            val ringtone = RingtoneManager.getActualDefaultRingtoneUri(app, RingtoneManager.TYPE_RINGTONE)
                ?: RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE)
            val alarm = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM)
            if (!play(app, ringtone) && !play(app, alarm)) {
                stop(app)
                return@post
            }
            handler.postDelayed({ stop(app) }, RING_MS)
        }
    }

    private fun play(ctx: Context, uri: Uri?): Boolean {
        if (uri == null) return false
        val p = MediaPlayer()
        return try {
            p.setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_ALARM)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                    .build()
            )
            p.setDataSource(ctx, uri)
            p.isLooping = true
            p.prepare()
            p.start()
            player = p
            true
        } catch (_: Exception) {
            p.release()
            false
        }
    }

    private fun stop(ctx: Context) {
        handler.removeCallbacksAndMessages(null)
        player?.let {
            try { it.stop() } catch (_: Exception) {}
            it.release()
        }
        player = null
        if (savedVolume >= 0) {
            val am = ctx.getSystemService(AudioManager::class.java)
            am.setStreamVolume(AudioManager.STREAM_ALARM, savedVolume, 0)
            savedVolume = -1
        }
    }
}
