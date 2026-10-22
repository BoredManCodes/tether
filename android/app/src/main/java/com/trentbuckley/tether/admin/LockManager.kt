package com.trentbuckley.tether.admin

import android.app.PendingIntent
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.provider.Telephony
import com.trentbuckley.tether.Prefs
import com.trentbuckley.tether.ui.LockActivity

/**
 * Applies/lifts the "everything but Phone and SMS" restriction using
 * Device Owner app suspension. We suspend every launchable app except
 * the default dialer, the default SMS app, and ourselves, rather than
 * using lock task mode, so the kid can still freely switch between
 * Phone and Messages instead of being pinned to one screen.
 */
object LockManager {

    private fun dpm(ctx: Context) = ctx.getSystemService(DevicePolicyManager::class.java)
    private fun adminComponent(ctx: Context) = ComponentName(ctx, TetherDeviceAdminReceiver::class.java)

    private fun allowedPackages(ctx: Context): Set<String> {
        val allowed = mutableSetOf(ctx.packageName)
        val tm = ctx.getSystemService(android.telecom.TelecomManager::class.java)
        tm?.defaultDialerPackage?.let { allowed.add(it) }
        Telephony.Sms.getDefaultSmsPackage(ctx)?.let { allowed.add(it) }
        return allowed
    }

    private fun allLaunchablePackages(ctx: Context): List<String> {
        val pm = ctx.packageManager
        val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        return pm.queryIntentActivities(intent, 0).map { it.activityInfo.packageName }.distinct()
    }

    fun lock(ctx: Context) {
        val manager = dpm(ctx)
        val admin = adminComponent(ctx)
        if (manager.isDeviceOwnerApp(ctx.packageName)) {
            val allowed = allowedPackages(ctx)
            val targets = allLaunchablePackages(ctx).filter { it !in allowed }
            for (pkg in targets) {
                try {
                    manager.setPackagesSuspended(admin, arrayOf(pkg), true)
                } catch (_: Exception) {
                    // Some system packages can't be suspended; skip them.
                }
            }
        }
        Prefs.setLocked(ctx, true)
        val intent = Intent(ctx, LockActivity::class.java).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        ctx.startActivity(intent)
    }

    fun unlock(ctx: Context) {
        val manager = dpm(ctx)
        val admin = adminComponent(ctx)
        if (manager.isDeviceOwnerApp(ctx.packageName)) {
            val targets = allLaunchablePackages(ctx)
            for (pkg in targets) {
                try {
                    manager.setPackagesSuspended(admin, arrayOf(pkg), false)
                } catch (_: Exception) {
                }
            }
        }
        Prefs.setLocked(ctx, false)
    }

    /** Silently uninstalls a user-installed package. Device Owner only; system apps,
     *  ourselves, the dialer and the SMS app are never touched. */
    fun uninstallPackage(ctx: Context, pkg: String) {
        val manager = dpm(ctx)
        if (!manager.isDeviceOwnerApp(ctx.packageName)) return
        if (pkg in allowedPackages(ctx)) return
        val info = try {
            ctx.packageManager.getApplicationInfo(pkg, 0)
        } catch (_: PackageManager.NameNotFoundException) {
            return // not installed, nothing to do
        }
        if ((info.flags and ApplicationInfo.FLAG_SYSTEM) != 0) return
        try {
            manager.setUninstallBlocked(adminComponent(ctx), pkg, false)
            // The installer needs a mutable PendingIntent to report back; nothing listens for it.
            val done = PendingIntent.getBroadcast(
                ctx, 0, Intent("com.trentbuckley.tether.UNINSTALL_DONE").setPackage(ctx.packageName),
                PendingIntent.FLAG_MUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
            )
            ctx.packageManager.packageInstaller.uninstall(pkg, done.intentSender)
        } catch (_: Exception) {
        }
    }

    /** Suspends or unsuspends one specific package. Used by AppRuleManager for
     *  per-app blocks and daily limits, independent of the whole-device lock. */
    fun setPackageSuspended(ctx: Context, pkg: String, suspended: Boolean) {
        val manager = dpm(ctx)
        if (!manager.isDeviceOwnerApp(ctx.packageName)) return
        if (pkg in allowedPackages(ctx)) return // never suspend ourselves/dialer/SMS
        try {
            manager.setPackagesSuspended(adminComponent(ctx), arrayOf(pkg), suspended)
        } catch (_: Exception) {
            // Package not installed, or a system package that can't be suspended.
        }
    }

    /** Hides or unhides one package (gone from launcher and settings, data kept).
     *  Same guard as suspension: never ourselves, the dialer or the SMS app. */
    fun setPackageHidden(ctx: Context, pkg: String, hidden: Boolean) {
        val manager = dpm(ctx)
        if (!manager.isDeviceOwnerApp(ctx.packageName)) return
        if (pkg in allowedPackages(ctx)) return
        try {
            manager.setApplicationHidden(adminComponent(ctx), pkg, hidden)
        } catch (_: Exception) {
            // Package not installed.
        }
    }
}
