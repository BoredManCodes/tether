package com.trentbuckley.tether.ui

import android.app.Activity
import android.os.Bundle
import android.view.WindowInsets
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import com.trentbuckley.tether.PinHash
import com.trentbuckley.tether.Prefs
import com.trentbuckley.tether.net.Api
import com.trentbuckley.tether.sync.TetherSyncService
import kotlin.concurrent.thread

/**
 * One-time setup screen: server URL + parent login (used only to mint a
 * device token), then a parent PIN for this device's lock screen and
 * secret dialcode. Reached from PinActivity on first dial, or directly
 * if nothing is enrolled yet.
 */
class SetupActivity : Activity() {

    private lateinit var serverUrl: EditText
    private lateinit var email: EditText
    private lateinit var password: EditText
    private lateinit var pin: EditText
    private lateinit var name: EditText
    private lateinit var trustedNumbers: EditText
    private lateinit var commandSecret: EditText
    private lateinit var status: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        actionBar?.hide()

        val root = LinearLayout(this)
        root.orientation = LinearLayout.VERTICAL
        root.setPadding(32, 64, 32, 32)
        root.setOnApplyWindowInsetsListener { view, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars())
            view.setPadding(32 + bars.left, 64 + bars.top, 32 + bars.right, 32 + bars.bottom)
            insets
        }

        serverUrl = EditText(this).apply { hint = "Server URL (https://tether.example.com)" }
        email = EditText(this).apply { hint = "Parent email" }
        password = EditText(this).apply {
            hint = "Parent password"
            inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD
        }
        name = EditText(this).apply { hint = "Device name (e.g. Kid's Pixel)" }
        pin = EditText(this).apply {
            hint = "New parent PIN (4-8 digits)"
            inputType = android.text.InputType.TYPE_CLASS_NUMBER or android.text.InputType.TYPE_NUMBER_VARIATION_PASSWORD
        }
        trustedNumbers = EditText(this).apply { hint = "Trusted numbers for SMS commands (comma-separated)" }
        commandSecret = EditText(this).apply { hint = "SMS command secret word" }
        status = TextView(this)
        val submit = Button(this).apply { text = "Enroll" }

        for (v in listOf(serverUrl, email, password, name, pin, trustedNumbers, commandSecret, submit, status)) root.addView(v)
        setContentView(root)

        if (Prefs.isEnrolled(this)) {
            serverUrl.setText(Prefs.serverUrl(this))
            trustedNumbers.setText(Prefs.trustedNumbers(this).joinToString(","))
            status.text = "Already enrolled. Re-enrolling replaces the device token."
        }

        submit.setOnClickListener { doEnroll() }
    }

    private fun doEnroll() {
        val url = serverUrl.text.toString().trim().trimEnd('/')
        val em = email.text.toString().trim()
        val pw = password.text.toString()
        val devName = name.text.toString().trim().ifBlank { "Phone" }
        val pinStr = pin.text.toString()

        if (url.isBlank() || em.isBlank() || pw.isBlank() || pinStr.length < 4) {
            status.text = "Fill in server URL, email, password, and a PIN of at least 4 digits."
            return
        }

        status.text = "Enrolling..."
        thread {
            try {
                val (deviceId, token) = Api.enroll(url, em, pw, devName)
                runOnUiThread {
                    Prefs.setServerUrl(this, url)
                    Prefs.saveEnrollment(this, deviceId, token)
                    Prefs.setParentPinHash(this, PinHash.hash(pinStr))
                    Prefs.setTrustedNumbers(this, trustedNumbers.text.toString())
                    if (commandSecret.text.isNotBlank()) Prefs.setCommandSecret(this, commandSecret.text.toString())
                    TetherSyncService.start(this)
                    Toast.makeText(this, "Enrolled", Toast.LENGTH_SHORT).show()
                    finish()
                }
            } catch (e: Exception) {
                runOnUiThread { status.text = "Enroll failed: ${e.message}" }
            }
        }
    }
}
