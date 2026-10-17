package com.trentbuckley.tether.ui

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.WindowInsets
import android.widget.LinearLayout
import android.widget.TextView
import com.trentbuckley.tether.Prefs

/**
 * Full-screen notice shown when the device is locked. Most other apps
 * are suspended at this point (see LockManager), so this mainly covers
 * the home screen / recents. Dial the secret code to reach PinActivity
 * and unlock with the parent PIN.
 */
class LockActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        actionBar?.hide()

        if (!Prefs.isLocked(this)) {
            finish()
            return
        }

        val root = LinearLayout(this)
        root.orientation = LinearLayout.VERTICAL
        root.setPadding(48, 200, 48, 48)
        root.setOnApplyWindowInsetsListener { view, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars())
            view.setPadding(48 + bars.left, 200 + bars.top, 48 + bars.right, 48 + bars.bottom)
            insets
        }
        root.addView(TextView(this).apply {
            text = "This phone is locked by a parent.\nPhone and Messages still work."
            textSize = 20f
        })
        setContentView(root)
    }

    override fun onResume() {
        super.onResume()
        if (!Prefs.isLocked(this)) finish()
    }

    override fun onBackPressed() {
        // Swallow back so the kid can't dismiss this onto the home screen.
    }

    companion object {
        fun intent(ctx: android.content.Context) = Intent(ctx, LockActivity::class.java).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
    }
}
