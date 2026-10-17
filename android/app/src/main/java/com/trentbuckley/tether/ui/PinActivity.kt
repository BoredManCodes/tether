package com.trentbuckley.tether.ui

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.WindowInsets
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import com.trentbuckley.tether.PinHash
import com.trentbuckley.tether.Prefs

/**
 * Target of the secret dialcode (*#*#<code>#*#*), delivered as a
 * SECRET_CODE broadcast by the dialer. Not enrolled yet -> go straight
 * to setup. Enrolled -> ask for the parent PIN before showing anything.
 */
class PinActivity : Activity() {

    private lateinit var input: EditText
    private lateinit var error: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        actionBar?.hide()

        if (!Prefs.isEnrolled(this)) {
            startActivity(Intent(this, SetupActivity::class.java))
            finish()
            return
        }

        val root = LinearLayout(this)
        root.orientation = LinearLayout.VERTICAL
        root.setPadding(32, 64, 32, 32)
        root.setOnApplyWindowInsetsListener { view, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars())
            view.setPadding(32 + bars.left, 64 + bars.top, 32 + bars.right, 32 + bars.bottom)
            insets
        }

        val label = TextView(this).apply { text = "Enter parent PIN" }
        input = EditText(this).apply {
            inputType = android.text.InputType.TYPE_CLASS_NUMBER or android.text.InputType.TYPE_NUMBER_VARIATION_PASSWORD
        }
        error = TextView(this)
        val ok = Button(this).apply { text = "Unlock" }

        for (v in listOf(label, input, ok, error)) root.addView(v)
        setContentView(root)

        ok.setOnClickListener {
            val stored = Prefs.parentPinHash(this)
            if (stored != null && PinHash.verify(input.text.toString(), stored)) {
                startActivity(Intent(this, SetupActivity::class.java))
                finish()
            } else {
                error.text = "Wrong PIN"
                input.setText("")
            }
        }
    }
}
