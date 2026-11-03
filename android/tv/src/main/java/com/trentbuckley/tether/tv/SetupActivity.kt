package com.trentbuckley.tether.tv

import android.app.Activity
import android.os.Bundle
import android.provider.Settings
import android.text.InputType
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import kotlin.concurrent.thread

/**
 * Enrol the TV with a parent login, and show whether the two services are on.
 * Also enrols from intent extras so it can be provisioned over adb:
 *   am start -n com.trentbuckley.tether.tv/.SetupActivity --es url ... --es email ... --es password ... --es name ...
 */
class SetupActivity : Activity() {

    private lateinit var url: EditText
    private lateinit var email: EditText
    private lateinit var password: EditText
    private lateinit var name: EditText
    private lateinit var status: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(96, 48, 96, 48)
        }
        url = EditText(this).apply { hint = "Server URL (https://tether.example.com)"; setText(Store.serverUrl(this@SetupActivity)) }
        email = EditText(this).apply { hint = "Parent email" }
        password = EditText(this).apply {
            hint = "Parent password"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
        }
        name = EditText(this).apply { hint = "Device name"; setText("Living room TV") }
        status = TextView(this).apply { textSize = 18f }
        val go = Button(this).apply { text = "Enroll"; setOnClickListener { enroll(url.text.toString(), email.text.toString(), password.text.toString(), name.text.toString()) } }
        for (v in listOf(url, email, password, name, go, status)) root.addView(v)
        setContentView(root)

        intent?.let {
            val u = it.getStringExtra("url"); val e = it.getStringExtra("email"); val p = it.getStringExtra("password")
            if (u != null && e != null && p != null) enroll(u, e, p, it.getStringExtra("name") ?: "Living room TV")
        }
    }

    override fun onResume() {
        super.onResume()
        refreshStatus()
    }

    private fun refreshStatus(extra: String = "") {
        val listenerOn = Settings.Secure.getString(contentResolver, "enabled_notification_listeners")
            ?.contains(packageName) == true
        val a11yOn = Settings.Secure.getString(contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
            ?.contains(packageName) == true
        status.text = buildString {
            if (extra.isNotEmpty()) appendLine(extra)
            appendLine("Enrolled: ${Store.isEnrolled(this@SetupActivity)}")
            appendLine("Watch reader on: $listenerOn")
            appendLine("Back presser on: $a11yOn")
            appendLine("Blocked channels: ${Store.blocked(this@SetupActivity).joinToString().ifEmpty { "none" }}")
            val meta = Store.lastMeta(this@SetupActivity)
            if (meta.isNotEmpty()) appendLine("Last media metadata:\n$meta")
        }
    }

    private fun enroll(rawUrl: String, em: String, pw: String, devName: String) {
        val base = rawUrl.trim().trimEnd('/')
        if (base.isEmpty() || em.isBlank() || pw.isEmpty()) { refreshStatus("Fill in URL, email and password."); return }
        refreshStatus("Enrolling...")
        thread {
            try {
                val token = Store.enroll(base, em.trim(), pw, devName.trim().ifEmpty { "TV" })
                Store.saveEnrollment(this, base, token)
                runOnUiThread { refreshStatus("Enrolled.") }
            } catch (e: Exception) {
                runOnUiThread { refreshStatus("Enroll failed: ${e.message}") }
            }
        }
    }
}
