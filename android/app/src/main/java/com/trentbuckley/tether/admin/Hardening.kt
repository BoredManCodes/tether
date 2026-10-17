package com.trentbuckley.tether.admin

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.os.Bundle
import android.os.UserManager
import com.trentbuckley.tether.net.TetherVpnService

/**
 * Device Owner policies that aren't tied to the lock or app rules:
 *  - private browsing off in Chrome and Edge (managed app restrictions),
 *  - DNS-over-HTTPS off in Chrome and Private DNS left in fallback mode,
 *    so lookups reach the Tether VPN,
 *  - Tether's VPN pinned as always-on, with the VPN settings locked.
 * Safe to call repeatedly; every step is idempotent and skipped when we
 * are not Device Owner.
 */
object Hardening {

    // package -> restrictions to push. Chrome reads these on its next launch.
    private val BROWSER_RESTRICTIONS: Map<String, Bundle.() -> Unit> = run {
        val chrome: Bundle.() -> Unit = {
            putInt("IncognitoModeAvailability", 1) // 1 = incognito disabled
            putString("DnsOverHttpsMode", "off")
        }
        mapOf(
            "com.android.chrome" to chrome,
            "com.chrome.beta" to chrome,
            "com.chrome.dev" to chrome,
            "com.chrome.canary" to chrome,
            "com.microsoft.emmx" to { putInt("InPrivateModeAvailability", 1) },
        )
    }

    fun apply(ctx: Context) {
        val dpm = ctx.getSystemService(DevicePolicyManager::class.java)
        if (dpm != null && dpm.isDeviceOwnerApp(ctx.packageName)) {
            val admin = ComponentName(ctx, TetherDeviceAdminReceiver::class.java)
            browsers(ctx, dpm, admin)
            vpn(ctx, dpm, admin)
        }
        // Without Device Owner this only works once VPN consent exists (user prompt,
        // or `appops set <pkg> ACTIVATE_VPN allow` on a test phone); start() checks.
        TetherVpnService.start(ctx)
    }

    private fun browsers(ctx: Context, dpm: DevicePolicyManager, admin: ComponentName) {
        val pm = ctx.packageManager
        for ((pkg, fill) in BROWSER_RESTRICTIONS) {
            try {
                pm.getPackageInfo(pkg, 0)
            } catch (_: PackageManager.NameNotFoundException) {
                continue
            }
            try {
                dpm.setApplicationRestrictions(admin, pkg, Bundle().apply(fill))
            } catch (_: Exception) {
            }
        }
    }

    private fun vpn(ctx: Context, dpm: DevicePolicyManager, admin: ComponentName) {
        try { dpm.setGlobalPrivateDnsModeOpportunistic(admin) } catch (_: Exception) {}
        try { dpm.addUserRestriction(admin, UserManager.DISALLOW_CONFIG_PRIVATE_DNS) } catch (_: Exception) {}
        // lockdown = false: if our VPN dies the phone keeps its connection instead of going offline.
        try { dpm.setAlwaysOnVpnPackage(admin, ctx.packageName, false) } catch (_: Exception) {}
        try { dpm.addUserRestriction(admin, UserManager.DISALLOW_CONFIG_VPN) } catch (_: Exception) {}
    }
}
