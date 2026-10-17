package com.trentbuckley.tether.sync

import android.content.Context
import android.content.pm.PackageManager

object AppLabelResolver {

    private val cache = HashMap<String, String?>()

    fun labelFor(ctx: Context, packageName: String?): String? {
        if (packageName.isNullOrBlank()) return null
        if (cache.containsKey(packageName)) return cache[packageName]

        val label = try {
            val pm = ctx.packageManager
            val info = pm.getApplicationInfo(packageName, 0)
            pm.getApplicationLabel(info).toString()
        } catch (_: PackageManager.NameNotFoundException) {
            null
        }
        cache[packageName] = label
        return label
    }
}
