package com.trentbuckley.tether.sync

import android.app.admin.DevicePolicyManager
import android.app.usage.UsageStatsManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.location.LocationManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.telephony.TelephonyManager
import android.provider.CallLog
import android.provider.Settings
import android.provider.Telephony
import androidx.core.app.NotificationManagerCompat
import com.trentbuckley.tether.Prefs
import com.trentbuckley.tether.admin.TetherDeviceAdminReceiver
import com.trentbuckley.tether.notif.TetherNotificationListener
import com.trentbuckley.tether.screen.TetherAccessibilityService
import org.json.JSONArray
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Gathers one sync batch's worth of data, matching the shape the server's
 * /api/device/sync endpoint expects. Each source tracks its own "since"
 * watermark in Prefs so we don't resend the kid's entire SMS history on
 * every tick.
 */
object DataCollector {

    private val dayFmt = SimpleDateFormat("yyyy-MM-dd", Locale.US)

    fun collect(ctx: Context): JSONObject {
        val out = JSONObject()
        out.put("status", status(ctx))
        out.put("locations", locations(ctx))
        out.put("sms", sms(ctx))
        out.put("calls", calls(ctx))
        out.put("notifications", notifications(ctx))
        out.put("usage", usage(ctx))
        out.put("apps", apps(ctx))
        out.put("dns", dns(ctx))
        return out
    }

    private fun status(ctx: Context): JSONObject {
        val bm = ctx.getSystemService(Context.BATTERY_SERVICE) as BatteryManager
        val pct = bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
        val charging = bm.isCharging
        val out = JSONObject()
            .put("battery_pct", pct)
            .put("charging", charging)
            .put("locked", Prefs.isLocked(ctx))
            .put("admin_active", isAdminActive(ctx))
            .put("accessibility_active", isAccessibilityActive(ctx))
            .put("notif_listener_active", isNotifListenerActive(ctx))
        cellular(ctx, out)
        wifi(ctx, out)
        return out
    }

    // Cell: network generation ("5G"/"4G"/...) plus bars (0-4) and dBm. Needs READ_PHONE_STATE.
    private fun cellular(ctx: Context, out: JSONObject) {
        try {
            val tm = ctx.getSystemService(Context.TELEPHONY_SERVICE) as TelephonyManager
            val gen = when (tm.dataNetworkType) {
                TelephonyManager.NETWORK_TYPE_NR -> "5G"
                TelephonyManager.NETWORK_TYPE_LTE -> "4G"
                TelephonyManager.NETWORK_TYPE_UMTS, TelephonyManager.NETWORK_TYPE_HSDPA,
                TelephonyManager.NETWORK_TYPE_HSUPA, TelephonyManager.NETWORK_TYPE_HSPA,
                TelephonyManager.NETWORK_TYPE_HSPAP, TelephonyManager.NETWORK_TYPE_EVDO_0,
                TelephonyManager.NETWORK_TYPE_EVDO_A, TelephonyManager.NETWORK_TYPE_EVDO_B -> "3G"
                TelephonyManager.NETWORK_TYPE_GPRS, TelephonyManager.NETWORK_TYPE_EDGE,
                TelephonyManager.NETWORK_TYPE_CDMA, TelephonyManager.NETWORK_TYPE_1xRTT -> "2G"
                else -> null
            }
            out.put("cell_type", gen)
            val ss = tm.signalStrength
            if (ss != null) {
                out.put("cell_level", ss.level)
                val dbm = ss.cellSignalStrengths.firstOrNull()?.dbm
                if (dbm != null && dbm != Int.MAX_VALUE) out.put("cell_dbm", dbm)
            }
        } catch (_: SecurityException) {
            // READ_PHONE_STATE not granted; leave cell fields unset.
        }
    }

    // Wi-Fi: SSID, bars (0-4) and RSSI. SSID is only readable with location permission and
    // location services on; otherwise Android returns "<unknown ssid>", which we drop.
    @Suppress("DEPRECATION")
    private fun wifi(ctx: Context, out: JSONObject) {
        try {
            val cm = ctx.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            val caps = cm.getNetworkCapabilities(cm.activeNetwork)
            val onWifi = caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
            out.put("wifi_connected", onWifi)
            if (!onWifi) return
            val wm = ctx.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            val info = wm.connectionInfo ?: return
            val ssid = info.ssid?.trim('"')
            if (!ssid.isNullOrEmpty() && ssid != WifiManager.UNKNOWN_SSID) out.put("wifi_ssid", ssid)
            if (info.rssi > -127) {
                out.put("wifi_rssi", info.rssi)
                out.put("wifi_level", wm.calculateSignalLevel(info.rssi))
            }
        } catch (_: SecurityException) {
            // Missing permission; leave Wi-Fi fields unset.
        }
    }

    // Tamper flags: reported every tick so the server can alert on a 1 -> 0
    // transition (see alerts.js#checkTamper). Keep this cheap; it runs on
    // every sync, not just at setup.
    private fun isAdminActive(ctx: Context): Boolean {
        val dpm = ctx.getSystemService(DevicePolicyManager::class.java)
        return dpm?.isDeviceOwnerApp(ctx.packageName) == true ||
            dpm?.isAdminActive(ComponentName(ctx, TetherDeviceAdminReceiver::class.java)) == true
    }

    private fun isAccessibilityActive(ctx: Context): Boolean {
        val enabled = Settings.Secure.getString(ctx.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: ""
        return enabled.contains("${ctx.packageName}/${TetherAccessibilityService::class.java.name}")
    }

    private fun isNotifListenerActive(ctx: Context): Boolean =
        NotificationManagerCompat.getEnabledListenerPackages(ctx).contains(ctx.packageName)

    private fun locations(ctx: Context): JSONArray {
        val arr = JSONArray()
        val lm = ctx.getSystemService(Context.LOCATION_SERVICE) as LocationManager
        for (provider in listOf(LocationManager.FUSED_PROVIDER, LocationManager.GPS_PROVIDER, LocationManager.NETWORK_PROVIDER)) {
            try {
                val loc = lm.getLastKnownLocation(provider) ?: continue
                arr.put(
                    JSONObject()
                        .put("ts", loc.time)
                        .put("lat", loc.latitude)
                        .put("lon", loc.longitude)
                        .put("accuracy", loc.accuracy)
                )
                break // first provider that has a fix is enough for one sync tick
            } catch (_: SecurityException) {
                // Permission not granted for this provider; try the next.
            }
        }
        return arr
    }

    private fun sms(ctx: Context): JSONArray {
        val arr = JSONArray()
        val tracker = SinceTracker(ctx, "sms")
        var maxTs = tracker.get()
        val cursor = ctx.contentResolver.query(
            Telephony.Sms.CONTENT_URI,
            arrayOf(Telephony.Sms.DATE, Telephony.Sms.TYPE, Telephony.Sms.ADDRESS, Telephony.Sms.BODY),
            "${Telephony.Sms.DATE} > ?",
            arrayOf(tracker.get().toString()),
            "${Telephony.Sms.DATE} ASC"
        )
        cursor?.use {
            while (it.moveToNext()) {
                val ts = it.getLong(0)
                val type = it.getInt(1)
                val direction = if (type == Telephony.Sms.MESSAGE_TYPE_SENT) "out" else "in"
                val address = it.getString(2) ?: ""
                arr.put(
                    JSONObject()
                        .put("ts", ts)
                        .put("direction", direction)
                        .put("address", address)
                        .put("body", it.getString(3) ?: "")
                        .put("contact", ContactResolver.nameFor(ctx, address))
                )
                if (ts > maxTs) maxTs = ts
            }
        }
        tracker.set(maxTs)
        return arr
    }

    private fun calls(ctx: Context): JSONArray {
        val arr = JSONArray()
        val tracker = SinceTracker(ctx, "calls")
        var maxTs = tracker.get()
        val cursor = ctx.contentResolver.query(
            CallLog.Calls.CONTENT_URI,
            arrayOf(CallLog.Calls.DATE, CallLog.Calls.TYPE, CallLog.Calls.NUMBER, CallLog.Calls.DURATION),
            "${CallLog.Calls.DATE} > ?",
            arrayOf(tracker.get().toString()),
            "${CallLog.Calls.DATE} ASC"
        )
        cursor?.use {
            while (it.moveToNext()) {
                val ts = it.getLong(0)
                val direction = when (it.getInt(1)) {
                    CallLog.Calls.OUTGOING_TYPE -> "out"
                    CallLog.Calls.MISSED_TYPE -> "missed"
                    else -> "in"
                }
                val number = it.getString(2) ?: ""
                arr.put(
                    JSONObject()
                        .put("ts", ts)
                        .put("direction", direction)
                        .put("number", number)
                        .put("duration_s", it.getInt(3))
                        .put("contact", ContactResolver.nameFor(ctx, number))
                )
                if (ts > maxTs) maxTs = ts
            }
        }
        tracker.set(maxTs)
        return arr
    }

    private fun notifications(ctx: Context): JSONArray {
        val arr = JSONArray()
        for (n in TetherNotificationListener.drain()) {
            arr.put(
                JSONObject()
                    .put("ts", n.ts)
                    .put("package", n.pkg)
                    .put("app_name", AppLabelResolver.labelFor(ctx, n.pkg))
                    .put("title", n.title)
                    .put("text", n.text)
            )
        }
        return arr
    }

    /** Every launcher-visible app, so the dashboard can list them with friendly names. */
    private fun apps(ctx: Context): JSONArray {
        val arr = JSONArray()
        val pm = ctx.packageManager
        val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        val seen = HashSet<String>()
        for (ri in pm.queryIntentActivities(intent, 0)) {
            val pkg = ri.activityInfo.packageName
            if (pkg == ctx.packageName || !seen.add(pkg)) continue
            val system = (ri.activityInfo.applicationInfo.flags and ApplicationInfo.FLAG_SYSTEM) != 0
            arr.put(
                JSONObject()
                    .put("package", pkg)
                    .put("app_name", ri.loadLabel(pm).toString())
                    .put("system", system)
            )
        }
        return arr
    }

    private fun dns(ctx: Context): JSONArray {
        val arr = JSONArray()
        for (q in DnsLog.drain()) {
            arr.put(
                JSONObject()
                    .put("ts", q.ts)
                    .put("domain", q.domain)
                    .put("package", q.pkg)
                    .put("app_name", AppLabelResolver.labelFor(ctx, q.pkg))
            )
        }
        return arr
    }

    private fun usage(ctx: Context): JSONArray {
        val arr = JSONArray()
        val usm = ctx.getSystemService(Context.USAGE_STATS_SERVICE) as UsageStatsManager
        val end = System.currentTimeMillis()
        val start = end - 24 * 60 * 60 * 1000L
        val day = dayFmt.format(Date(end))
        val stats = usm.queryUsageStats(UsageStatsManager.INTERVAL_DAILY, start, end) ?: return arr
        for (s in stats) {
            if (s.totalTimeInForeground <= 0) continue
            arr.put(
                JSONObject()
                    .put("day", day)
                    .put("package", s.packageName)
                    .put("app_name", AppLabelResolver.labelFor(ctx, s.packageName))
                    .put("foreground_ms", s.totalTimeInForeground)
            )
        }
        return arr
    }

    /** Per-source "last ts we already sent" watermark, stored directly via Prefs' SharedPreferences file. */
    private class SinceTracker(private val ctx: Context, private val key: String) {
        private val sp = ctx.getSharedPreferences("tether_sync_watermarks", Context.MODE_PRIVATE)
        fun get(): Long = sp.getLong(key, 0L)
        fun set(value: Long) { sp.edit().putLong(key, value).apply() }
    }
}
