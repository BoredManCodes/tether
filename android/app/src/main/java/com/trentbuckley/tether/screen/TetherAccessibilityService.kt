package com.trentbuckley.tether.screen

import android.accessibilityservice.AccessibilityService
import android.os.Handler
import android.os.Looper
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import com.trentbuckley.tether.sync.KeywordWatch
import kotlin.concurrent.thread

/**
 * Holds the screenshot permission (canTakeScreenshot, API 30+) that
 * ScreenStream calls into, and reads on-screen and typed text for keyword
 * alerts. Device Owner can't enable accessibility services on its own, so
 * this has to be turned on once over adb during provisioning.
 */
class TetherAccessibilityService : AccessibilityService() {

    private val handler = Handler(Looper.getMainLooper())
    @Volatile private var scanPending = false

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
        event ?: return
        val pkg = event.packageName?.toString() ?: return
        if (pkg == packageName) return

        if (event.eventType == AccessibilityEvent.TYPE_VIEW_TEXT_CHANGED) {
            if (!event.isPassword) KeywordWatch.checkScreen(this, event.text.joinToString(" "), pkg)
            return
        }
        // Screens change in bursts; one scan shortly after the burst is enough.
        if (scanPending) return
        scanPending = true
        handler.postDelayed({
            scanPending = false
            thread { scan() }
        }, SCAN_DELAY_MS)
    }

    private fun scan() {
        val root = rootInActiveWindow ?: return
        val pkg = root.packageName?.toString() ?: return
        // The shade shows notification text we already check; the keyboard shows suggestions, not what was written.
        if (pkg == packageName || pkg == "com.android.systemui" || pkg.contains("inputmethod")) return
        val out = StringBuilder()
        collect(root, out, intArrayOf(MAX_NODES))
        KeywordWatch.checkScreen(this, out.toString(), pkg)
    }

    @Suppress("DEPRECATION")
    private fun collect(node: AccessibilityNodeInfo?, out: StringBuilder, budget: IntArray) {
        if (node == null || budget[0] <= 0 || out.length > MAX_CHARS) return
        budget[0]--
        if (node.isVisibleToUser && !node.isPassword) {
            node.text?.let { out.append(it).append('\n') }
            node.contentDescription?.let { out.append(it).append('\n') }
        }
        for (i in 0 until node.childCount) {
            val child = node.getChild(i)
            collect(child, out, budget)
            child?.recycle()
        }
    }

    override fun onInterrupt() {}

    companion object {
        private const val SCAN_DELAY_MS = 1500L
        private const val MAX_NODES = 2000
        private const val MAX_CHARS = 30_000
        var instance: TetherAccessibilityService? = null
    }
}
