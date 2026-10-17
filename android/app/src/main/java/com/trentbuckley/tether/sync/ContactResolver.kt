package com.trentbuckley.tether.sync

import android.content.Context
import android.provider.ContactsContract

/**
 * Resolves a phone number to the phone's own contact name via
 * ContactsContract.PhoneLookup, so the dashboard can group SMS/calls by
 * contact instead of raw numbers. Cached in-memory per sync process since
 * the same handful of numbers repeat across a batch; the cache is cleared
 * between app process restarts, which is fine since lookups are cheap.
 */
object ContactResolver {

    private val cache = HashMap<String, String?>()

    fun nameFor(ctx: Context, number: String?): String? {
        if (number.isNullOrBlank()) return null
        cache[number]?.let { return it }
        if (cache.containsKey(number)) return null // cached miss

        val uri = android.net.Uri.withAppendedPath(
            ContactsContract.PhoneLookup.CONTENT_FILTER_URI,
            android.net.Uri.encode(number)
        )
        val name = try {
            ctx.contentResolver.query(
                uri,
                arrayOf(ContactsContract.PhoneLookup.DISPLAY_NAME),
                null, null, null
            )?.use { c -> if (c.moveToFirst()) c.getString(0) else null }
        } catch (_: Exception) {
            null
        }
        cache[number] = name
        return name
    }
}
