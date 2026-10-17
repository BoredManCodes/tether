package com.trentbuckley.tether.sync

import android.content.Context
import com.trentbuckley.tether.Prefs
import com.trentbuckley.tether.admin.LockManager
import com.trentbuckley.tether.net.Api
import org.json.JSONArray
import java.util.Calendar

/**
 * Fetches /api/device/schedule and locks/unlocks based on it. Edge-triggered,
 * not level-triggered: we only call LockManager when the schedule-computed
 * state changes, and we only undo a lock we ourselves put in place
 * (tracked via Prefs.isScheduleLockActive). That way a manual LOCK from the
 * dashboard or an SMS command doesn't get silently reverted by the next
 * schedule check, and a manual UNLOCK during a scheduled window doesn't get
 * immediately re-locked until the next window boundary.
 *
 * days is a string of digits, 0=Sunday..6=Saturday (matches JS Date.getDay(),
 * since that's what the dashboard's schedule editor uses). start_min/end_min
 * are minutes since local midnight; end_min <= start_min means the window
 * wraps past midnight (e.g. 21:00-07:00 bedtime).
 */
object ScheduleManager {

    fun checkAndApply(ctx: Context) {
        if (!Prefs.isEnrolled(ctx)) return
        val raw = try {
            Api.get(ctx, "/api/device/schedule")
        } catch (_: Exception) {
            return
        }
        val schedules = try {
            JSONArray(raw)
        } catch (_: Exception) {
            return
        }

        val cal = Calendar.getInstance()
        val today = cal.get(Calendar.DAY_OF_WEEK) - 1 // Calendar.SUNDAY == 1 -> 0
        val yesterday = (today + 6) % 7
        val minuteOfDay = cal.get(Calendar.HOUR_OF_DAY) * 60 + cal.get(Calendar.MINUTE)

        var shouldLock = false
        for (i in 0 until schedules.length()) {
            val s = schedules.getJSONObject(i)
            val days = s.getString("days")
            val start = s.getInt("start_min")
            val end = s.getInt("end_min")
            val hit = if (start < end) {
                days.contains(today.toString()) && minuteOfDay in start until end
            } else {
                (days.contains(today.toString()) && minuteOfDay >= start) ||
                    (days.contains(yesterday.toString()) && minuteOfDay < end)
            }
            if (hit) {
                shouldLock = true
                break
            }
        }

        val wasActive = Prefs.isScheduleLockActive(ctx)
        if (shouldLock && !wasActive) {
            LockManager.lock(ctx)
            Prefs.setScheduleLockActive(ctx, true)
        } else if (!shouldLock && wasActive) {
            LockManager.unlock(ctx)
            Prefs.setScheduleLockActive(ctx, false)
        }
    }
}
