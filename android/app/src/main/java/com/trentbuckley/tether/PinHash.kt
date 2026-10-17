package com.trentbuckley.tether

import java.security.MessageDigest
import java.security.SecureRandom

/** Salted hash for the local parent PIN. Not meant to resist a determined attacker with
 * device access, just to keep the PIN out of plain text in SharedPreferences. */
object PinHash {
    fun hash(pin: String, salt: ByteArray = randomSalt()): String {
        val digest = MessageDigest.getInstance("SHA-256")
        digest.update(salt)
        val out = digest.digest(pin.toByteArray())
        return salt.joinToString("") { "%02x".format(it) } + ":" + out.joinToString("") { "%02x".format(it) }
    }

    fun verify(pin: String, stored: String): Boolean {
        val parts = stored.split(":")
        if (parts.size != 2) return false
        val salt = parts[0].chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        return hash(pin, salt) == stored
    }

    private fun randomSalt(): ByteArray = ByteArray(16).also { SecureRandom().nextBytes(it) }
}
