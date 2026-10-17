package com.trentbuckley.tether.sync

import android.content.Context
import android.provider.MediaStore
import com.trentbuckley.tether.Prefs
import java.io.OutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID

/**
 * Uploads new camera-roll photos as multipart/form-data to
 * /api/device/media. Runs on whatever thread calls it; callers
 * (SCAN_MEDIA command, periodic tick) are expected to already be
 * off the main thread.
 */
object MediaUploader {

    private const val MAX_PER_SCAN = 20

    fun scanAndUpload(ctx: Context) {
        val sp = ctx.getSharedPreferences("tether_sync_watermarks", Context.MODE_PRIVATE)
        var since = sp.getLong("media", 0L)
        var maxDateAdded = since

        val projection = arrayOf(MediaStore.Images.Media._ID, MediaStore.Images.Media.DATE_ADDED)
        val cursor = ctx.contentResolver.query(
            MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
            projection,
            "${MediaStore.Images.Media.DATE_ADDED} > ?",
            arrayOf(since.toString()),
            "${MediaStore.Images.Media.DATE_ADDED} ASC LIMIT $MAX_PER_SCAN"
        ) ?: return

        cursor.use {
            while (it.moveToNext()) {
                val id = it.getLong(0)
                val dateAdded = it.getLong(1)
                val uri = MediaStore.Images.Media.EXTERNAL_CONTENT_URI.buildUpon().appendPath(id.toString()).build()
                try {
                    ctx.contentResolver.openInputStream(uri)?.use { input ->
                        upload(ctx, input.readBytes(), "photo", dateAdded * 1000)
                    }
                } catch (_: Exception) {
                    // Skip files we can't read (deleted mid-scan, permission edge cases).
                }
                if (dateAdded > maxDateAdded) maxDateAdded = dateAdded
            }
        }
        if (maxDateAdded > since) sp.edit().putLong("media", maxDateAdded).apply()
    }

    private fun upload(ctx: Context, bytes: ByteArray, kind: String, tsMs: Long) {
        val base = Prefs.serverUrl(ctx) ?: return
        val token = Prefs.deviceToken(ctx) ?: return
        val boundary = "tether-${UUID.randomUUID()}"
        val conn = URL("$base/api/device/media").openConnection() as HttpURLConnection
        conn.requestMethod = "POST"
        conn.doOutput = true
        conn.connectTimeout = 20_000
        conn.readTimeout = 20_000
        conn.setRequestProperty("Authorization", "Bearer $token")
        conn.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")

        conn.outputStream.use { out ->
            writeField(out, boundary, "kind", kind)
            writeField(out, boundary, "ts", tsMs.toString())
            writeFile(out, boundary, "file", "upload.jpg", bytes)
            out.write("--$boundary--\r\n".toByteArray())
        }
        conn.inputStream.use { it.readBytes() }
        conn.disconnect()
    }

    private fun writeField(out: OutputStream, boundary: String, name: String, value: String) {
        out.write("--$boundary\r\n".toByteArray())
        out.write("Content-Disposition: form-data; name=\"$name\"\r\n\r\n".toByteArray())
        out.write("$value\r\n".toByteArray())
    }

    private fun writeFile(out: OutputStream, boundary: String, field: String, filename: String, bytes: ByteArray) {
        out.write("--$boundary\r\n".toByteArray())
        out.write("Content-Disposition: form-data; name=\"$field\"; filename=\"$filename\"\r\n".toByteArray())
        out.write("Content-Type: image/jpeg\r\n\r\n".toByteArray())
        out.write(bytes)
        out.write("\r\n".toByteArray())
    }
}
