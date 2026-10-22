package com.trentbuckley.tether.sms

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.telephony.SmsManager
import com.trentbuckley.tether.Prefs
import com.trentbuckley.tether.admin.LockManager
import com.trentbuckley.tether.admin.Ringer
import com.trentbuckley.tether.sync.KeywordWatch
import com.trentbuckley.tether.sync.LocationFetcher
import com.trentbuckley.tether.sync.TetherSyncService
import kotlin.concurrent.thread

/**
 * Handles SMS commands from trusted numbers. Sender numbers are
 * spoofable over SMS, so a command only runs if:
 *   1. the sender's normalized number is on the trusted list, AND
 *   2. the message contains the configured secret word.
 * Recognized commands: GETLOC, LOCK, UNLOCK, BATT, RING.
 */
class SmsReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != "android.provider.Telephony.SMS_RECEIVED") return
        val messages = android.provider.Telephony.Sms.Intents.getMessagesFromIntent(intent) ?: return
        if (messages.isEmpty()) return

        val sender = messages[0].originatingAddress ?: return
        val body = messages.joinToString("") { it.messageBody ?: "" }

        // The messaging app's own notification usually trips this too; the shared cooldown dedupes.
        KeywordWatch.check(context, body, System.currentTimeMillis(), delayMs = 1500)

        if (!isTrusted(context, sender)) return
        val secret = Prefs.commandSecret(context)
        if (secret.isNullOrBlank() || !body.contains(secret, ignoreCase = true)) return

        val withoutSecret = body.replace(secret, "", ignoreCase = true).trim()
        val command = withoutSecret.split(Regex("\\s+")).firstOrNull()?.uppercase() ?: return

        handleCommand(context, sender, command, goAsync())
    }

    private fun isTrusted(context: Context, sender: String): Boolean {
        val normalized = normalize(sender)
        return Prefs.trustedNumbers(context).any { normalize(it) == normalized }
    }

    private fun normalize(number: String): String = number.filter { it.isDigit() }.takeLast(10)

    private fun handleCommand(context: Context, replyTo: String, command: String, pending: PendingResult) {
        thread {
            try {
                val reply = runCommand(context, command) ?: return@thread
                try {
                    val sms = context.getSystemService(SmsManager::class.java)
                    sms.sendTextMessage(replyTo, null, reply, null, null)
                } catch (_: Exception) {
                    // Best effort; the dashboard still reflects the action either way.
                }
            } finally {
                pending.finish()
            }
        }
    }

    private fun runCommand(context: Context, command: String): String? = when (command) {
        "GETLOC" -> {
            val loc = LocationFetcher.fetch(context)
            // The fresh fix is now the last known one, so the sync uploads it too.
            TetherSyncService.requestImmediateSync(context)
            if (loc == null) {
                "No location fix."
            } else {
                val ageMin = (System.currentTimeMillis() - loc.time) / 60_000
                val age = if (ageMin >= 2) " (${ageMin}m old)" else ""
                "%.5f,%.5f +/-%dm%s https://maps.google.com/?q=%.5f,%.5f"
                    .format(loc.latitude, loc.longitude, loc.accuracy.toInt(), age, loc.latitude, loc.longitude)
            }
        }
        "LOCK" -> {
            LockManager.lock(context)
            "Locked."
        }
        "UNLOCK" -> {
            LockManager.unlock(context)
            "Unlocked."
        }
        "BATT" -> {
            val bm = context.getSystemService(Context.BATTERY_SERVICE) as android.os.BatteryManager
            val pct = bm.getIntProperty(android.os.BatteryManager.BATTERY_PROPERTY_CAPACITY)
            "Battery: $pct%"
        }
        "RING" -> {
            Ringer.ring(context)
            "Ringing."
        }
        else -> null
    }
}
