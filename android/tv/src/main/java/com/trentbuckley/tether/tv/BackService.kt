package com.trentbuckley.tether.tv

import android.accessibilityservice.AccessibilityService
import android.graphics.Color
import android.graphics.PixelFormat
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.KeyEvent
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.widget.TextView

/**
 * Three jobs, all of which need an accessibility service:
 *  - press Back (only an accessibility service can inject a global Back),
 *  - show the "blocked" popup (an accessibility overlay needs no extra permission),
 *  - watch remote key presses for the parent bypass code.
 *
 * The bypass code is never drawn. Key presses are matched against salted hashes
 * from the server and pass through to the app untouched, except while the
 * parent is typing the bypass length.
 */
class BackService : AccessibilityService() {

    private val main = Handler(Looper.getMainLooper())
    private var overlay: TextView? = null
    private val hideOverlay = Runnable { removeOverlay() }

    private val typed = StringBuilder()           // recent remote presses, digits and U/D/L/R/K
    private val clearTyped = Runnable { typed.setLength(0) }

    private var asking = false                    // true while the parent types the bypass length
    private val minutes = StringBuilder()
    private val askTimeout = Runnable { if (asking) finishAsk(confirm = true) }
    private var lastBlockedShown = 0L

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
    }

    override fun onDestroy() {
        if (instance === this) instance = null
        removeOverlay()
        super.onDestroy()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}
    override fun onInterrupt() {}

    // ---------- overlay ----------

    private fun showText(text: String, ms: Long) {
        removeOverlay()
        val tv = TextView(this).apply {
            this.text = text
            textSize = 30f
            setTextColor(Color.WHITE)
            setBackgroundColor(Color.argb(235, 20, 20, 24))
            gravity = Gravity.CENTER
            setPadding(80, 48, 80, 48)
        }
        val lp = WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT, WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE,
            PixelFormat.TRANSLUCENT,
        ).apply { gravity = Gravity.CENTER }
        try {
            (getSystemService(WINDOW_SERVICE) as WindowManager).addView(tv, lp)
            overlay = tv
            main.removeCallbacks(hideOverlay)
            if (ms > 0) main.postDelayed(hideOverlay, ms)
        } catch (_: Exception) {}
    }

    private fun updateText(text: String) { overlay?.text = text }

    private fun removeOverlay() {
        main.removeCallbacks(hideOverlay)
        overlay?.let { try { (getSystemService(WINDOW_SERVICE) as WindowManager).removeView(it) } catch (_: Exception) {} }
        overlay = null
    }

    // ---------- remote keys ----------

    override fun onKeyEvent(event: KeyEvent): Boolean {
        if (event.action != KeyEvent.ACTION_DOWN) return asking && event.keyCode != KeyEvent.KEYCODE_BACK && keyChar(event) != null
        val ch = keyChar(event)

        if (asking) {
            when {
                event.keyCode == KeyEvent.KEYCODE_BACK -> { finishAsk(confirm = false); return true }
                ch == 'K' -> { finishAsk(confirm = true); return true }
                ch != null && ch.isDigit() -> {
                    if (minutes.length < 3) minutes.append(ch)
                    updateText(askText())
                    main.removeCallbacks(askTimeout); main.postDelayed(askTimeout, ASK_MS)
                    return true
                }
            }
            return true
        }

        if (ch == null) return false
        typed.append(ch)
        if (typed.length > MAX_CODE) typed.delete(0, typed.length - MAX_CODE)
        main.removeCallbacks(clearTyped); main.postDelayed(clearTyped, IDLE_MS)
        if (codeTyped()) {
            typed.setLength(0)
            if (Store.bypassActive(this)) {
                Store.setBypassUntil(this, 0L)
                showText("Blocking is back on", 3_000)
            } else {
                asking = true; minutes.setLength(0)
                showText(askText(), 0)
                main.postDelayed(askTimeout, ASK_MS)
            }
        }
        return false // never swallow presses while a code is being typed
    }

    private fun keyChar(e: KeyEvent): Char? = when (e.keyCode) {
        in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9 -> '0' + (e.keyCode - KeyEvent.KEYCODE_0)
        in KeyEvent.KEYCODE_NUMPAD_0..KeyEvent.KEYCODE_NUMPAD_9 -> '0' + (e.keyCode - KeyEvent.KEYCODE_NUMPAD_0)
        KeyEvent.KEYCODE_DPAD_UP -> 'U'
        KeyEvent.KEYCODE_DPAD_DOWN -> 'D'
        KeyEvent.KEYCODE_DPAD_LEFT -> 'L'
        KeyEvent.KEYCODE_DPAD_RIGHT -> 'R'
        KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER -> 'K'
        else -> null
    }

    private fun codeTyped(): Boolean {
        val codes = Store.bypassCodes(this)
        if (codes.isEmpty()) return false
        val s = typed.toString()
        for (entry in codes) {
            val salt = entry.substringBefore(':'); val hash = entry.substringAfter(':')
            for (len in MIN_CODE..s.length) {
                if (Store.sha256Hex("$salt:${s.takeLast(len)}") == hash) return true
            }
        }
        return false
    }

    private fun askText() = "Parent mode\nBlocking off for how many minutes?\n" +
        "${if (minutes.isEmpty()) "60" else minutes} min\nType minutes, then OK. Back to cancel."

    private fun finishAsk(confirm: Boolean) {
        main.removeCallbacks(askTimeout)
        asking = false
        if (!confirm) { removeOverlay(); minutes.setLength(0); return }
        val m = (minutes.toString().toIntOrNull() ?: 60).coerceIn(1, 720)
        minutes.setLength(0)
        Store.setBypassUntil(this, System.currentTimeMillis() + m * 60_000L)
        showText("Blocking is off for $m min", 4_000)
    }

    companion object {
        private const val MIN_CODE = 6
        private const val MAX_CODE = 16
        private const val IDLE_MS = 8_000L
        private const val ASK_MS = 20_000L

        @Volatile var instance: BackService? = null

        /** Returns false when the service is not running. */
        fun back(): Boolean = instance?.performGlobalAction(GLOBAL_ACTION_BACK) ?: false

        /** Popup after Back. Repeat calls inside a few seconds are ignored so it does not flicker. */
        fun showBlocked(channel: String?) {
            val s = instance ?: return
            s.main.post {
                val now = System.currentTimeMillis()
                if (now - s.lastBlockedShown < 6_000L || s.asking) return@post
                s.lastBlockedShown = now
                val who = if (channel.isNullOrBlank()) "" else "\n$channel"
                s.showText("This content is blocked$who\nAsk a parent.", 5_000)
            }
        }
    }
}
