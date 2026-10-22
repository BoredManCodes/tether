package com.trentbuckley.tether.sync

import android.app.usage.UsageStatsManager
import android.content.Context
import com.trentbuckley.tether.Prefs
import com.trentbuckley.tether.admin.LockManager
import com.trentbuckley.tether.net.Api
import org.json.JSONArray
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Fetches /api/device/apprules and suspends packages that are flat-out
 * blocked or have gone over their daily foreground-time limit, mirroring
 * how ScheduleManager drives LockManager for the bedtime lock. Tracked
 * separately from the schedule lock so the two don't fight: a package we
 * suspended for a limit stays suspended through a schedule unlock, and a
 * schedule lock doesn't undo a limit we're no longer over.
 *
 * Limit-suspended packages are remembered in Prefs so a local day change
 * lifts them again without needing a server round trip to find out usage
 * reset to zero.
 */
object AppRuleManager {

    private val dayFmt = SimpleDateFormat("yyyy-MM-dd", Locale.US)

    fun checkAndApply(ctx: Context) {
        if (!Prefs.isEnrolled(ctx)) return
        // Cache the last good rule set: a reinstall that happens while the phone is
        // offline still has to be uninstalled.
        val cache = ctx.getSharedPreferences("tether_rules_cache", Context.MODE_PRIVATE)
        val raw = try {
            Api.get(ctx, "/api/device/apprules").also { cache.edit().putString("rules", it).apply() }
        } catch (_: Exception) {
            cache.getString("rules", null) ?: return
        }
        val rules = try { JSONArray(raw) } catch (_: Exception) { return }

        val today = dayFmt.format(Date())
        val limitSuspendedToday = Prefs.limitSuspendedPackages(ctx, today)
        val stillLimited = mutableSetOf<String>()
        val blockedNow = mutableSetOf<String>()

        for (i in 0 until rules.length()) {
            val r = rules.getJSONObject(i)
            val pkg = r.getString("package")
            // The server sends SQLite 0/1, and org.json's optBoolean only accepts true/false.
            val blocked = r.optInt("blocked", 0) != 0
            val limitMin = if (r.isNull("daily_limit_min")) null else r.getInt("daily_limit_min")

            if (blocked) {
                LockManager.setPackageSuspended(ctx, pkg, true)
                LockManager.setPackageHidden(ctx, pkg, true)
                blockedNow.add(pkg)
                // Rule outlives the app, so a reinstall is removed on the next check or install event.
                if (r.optInt("uninstall", 0) != 0) LockManager.uninstallPackage(ctx, pkg)
                continue
            }
            if (limitMin != null) {
                val overToday = limitSuspendedToday.contains(pkg) || foregroundMinutesToday(ctx, pkg) >= limitMin
                if (overToday) {
                    LockManager.setPackageSuspended(ctx, pkg, true)
                    stillLimited.add(pkg)
                    continue
                }
            }
            // Neither blocked nor over its limit: make sure it's not left
            // suspended from an earlier check this same day.
            if (limitSuspendedToday.contains(pkg)) LockManager.setPackageSuspended(ctx, pkg, false)
        }
        Prefs.setLimitSuspendedPackages(ctx, today, stillLimited)

        // Blocked before, not now: bring it back, unless today's limit still holds it.
        for (pkg in Prefs.blockedHiddenPackages(ctx) - blockedNow) {
            LockManager.setPackageHidden(ctx, pkg, false)
            if (pkg !in stillLimited) LockManager.setPackageSuspended(ctx, pkg, false)
        }
        Prefs.setBlockedHiddenPackages(ctx, blockedNow)
    }

    private fun foregroundMinutesToday(ctx: Context, pkg: String): Int {
        val usm = ctx.getSystemService(Context.USAGE_STATS_SERVICE) as UsageStatsManager
        val cal = java.util.Calendar.getInstance()
        cal.set(java.util.Calendar.HOUR_OF_DAY, 0); cal.set(java.util.Calendar.MINUTE, 0)
        cal.set(java.util.Calendar.SECOND, 0); cal.set(java.util.Calendar.MILLISECOND, 0)
        val start = cal.timeInMillis
        val end = System.currentTimeMillis()
        val stats = usm.queryUsageStats(UsageStatsManager.INTERVAL_DAILY, start, end) ?: return 0
        val ms = stats.firstOrNull { it.packageName == pkg }?.totalTimeInForeground ?: 0L
        return (ms / 60000L).toInt()
    }
}
