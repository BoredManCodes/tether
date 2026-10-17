package com.trentbuckley.tether.admin

import android.app.admin.DeviceAdminReceiver
import android.content.Context
import android.content.Intent

/**
 * Required receiver for device owner mode. We don't override much here;
 * device owner privileges are checked via DevicePolicyManager.isDeviceOwnerApp()
 * wherever we need them, not through callbacks on this receiver.
 */
class TetherDeviceAdminReceiver : DeviceAdminReceiver() {

    override fun onEnabled(context: Context, intent: Intent) {
        super.onEnabled(context, intent)
    }

    override fun onDisabled(context: Context, intent: Intent) {
        super.onDisabled(context, intent)
    }
}
