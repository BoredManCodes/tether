package com.trentbuckley.tether.screen

import android.accessibilityservice.AccessibilityService
import android.content.Context
import android.graphics.Bitmap
import android.graphics.ColorSpace
import android.os.Handler
import android.os.Looper
import com.trentbuckley.tether.net.Api
import com.trentbuckley.tether.sync.MediaUploader
import java.io.ByteArrayOutputStream
import kotlin.concurrent.thread

/**
 * Drives periodic screenshots through the accessibility service's
 * takeScreenshot() (API 30+) and posts each one as a JPEG frame. Apps
 * flagged FLAG_SECURE (banking apps, some password managers) come back
 * black; that's an Android platform restriction we can't get around
 * without MediaProjection, which would mean a consent dialog every time.
 *
 * SCREENSHOT just grabs one frame; SCREEN_START/STOP loop it at ~1fps
 * for the live-view tab on the dashboard.
 */
object ScreenStream {

    private const val INTERVAL_MS = 1000L
    private val handler = Handler(Looper.getMainLooper())
    private var streaming = false

    fun start(ctx: Context) {
        if (streaming) return
        streaming = true
        tick(ctx)
    }

    fun stop() {
        streaming = false
    }

    fun captureOnce(ctx: Context) {
        captureAndSend(ctx)
    }

    private fun tick(ctx: Context) {
        if (!streaming) return
        captureAndSend(ctx)
        handler.postDelayed({ tick(ctx) }, INTERVAL_MS)
    }

    /** One frame saved to the media library (not the live view), stamped with [tsMs]. */
    fun captureToMedia(ctx: Context, tsMs: Long) {
        captureAndSend(ctx) { jpeg -> MediaUploader.upload(ctx, jpeg, "screenshot", tsMs) }
    }

    private fun captureAndSend(ctx: Context, sink: (ByteArray) -> Unit = { Api.postBinary(ctx, "/api/device/screen", "image/jpeg", it) }) {
        val service = TetherAccessibilityService.instance ?: return
        service.takeScreenshot(
            android.view.Display.DEFAULT_DISPLAY,
            { it.run() },
            object : AccessibilityService.TakeScreenshotCallback {
                override fun onSuccess(result: AccessibilityService.ScreenshotResult) {
                    thread {
                        try {
                            val bitmap = toBitmap(result) ?: return@thread
                            val jpeg = toJpeg(bitmap)
                            sink(jpeg)
                        } catch (_: Exception) {
                            // Dropped frame; the next tick will try again.
                        }
                    }
                }

                override fun onFailure(errorCode: Int) {
                    // No fix available for most failure codes (e.g. interrupted, invalid display); just skip this frame.
                }
            }
        )
    }

    private fun toBitmap(result: AccessibilityService.ScreenshotResult): Bitmap? {
        return try {
            Bitmap.wrapHardwareBuffer(result.hardwareBuffer, result.colorSpace ?: ColorSpace.get(ColorSpace.Named.SRGB))
                ?.copy(Bitmap.Config.ARGB_8888, false)
        } finally {
            result.hardwareBuffer.close()
        }
    }

    private fun toJpeg(bitmap: Bitmap): ByteArray {
        val out = ByteArrayOutputStream()
        bitmap.compress(Bitmap.CompressFormat.JPEG, 60, out)
        bitmap.recycle()
        return out.toByteArray()
    }
}
