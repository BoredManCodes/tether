package com.trentbuckley.tether.screen

import android.accessibilityservice.AccessibilityService
import android.view.accessibility.AccessibilityEvent

/**
 * Exists mainly to hold the screenshot permission (canTakeScreenshot,
 * API 30+) that ScreenStream calls into. Device Owner can't enable
 * accessibility services on its own, so this has to be turned on once
 * over adb during provisioning.
 */
class TetherAccessibilityService : AccessibilityService() {

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        // Binding an accessibility service can start the process in the
        // background, where TetherApp's own start is refused; retry here.
        if (com.trentbuckley.tether.Prefs.isEnrolled(this)) {
            com.trentbuckley.tether.sync.TetherSyncService.start(this)
        }
    }

    override fun onDestroy() {
        instance = null
        super.onDestroy()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // We don't care about UI events, only the takeScreenshot() capability.
    }

    override fun onInterrupt() {}

    companion object {
        var instance: TetherAccessibilityService? = null
    }
}
