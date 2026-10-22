package com.trentbuckley.tether

import android.content.Context
import android.content.SharedPreferences

/**
 * Small wrapper around a SharedPreferences file for enrollment state and
 * local settings. Nothing here is encrypted; the device token is a bearer
 * token scoped to this one device, not a password.
 */
object Prefs {
    private const val FILE = "tether_prefs"

    private const val KEY_SERVER_URL = "server_url"
    private const val KEY_DEVICE_TOKEN = "device_token"
    private const val KEY_DEVICE_ID = "device_id"
    private const val KEY_PARENT_PIN_HASH = "parent_pin_hash"
    private const val KEY_LOCKED = "locked"
    private const val KEY_TRUSTED_NUMBERS = "trusted_numbers"
    private const val KEY_COMMAND_SECRET = "command_secret"
    private const val KEY_SCHEDULE_LOCK_ACTIVE = "schedule_lock_active"
    private const val KEY_LIMIT_SUSPENDED_DAY = "limit_suspended_day"
    private const val KEY_LIMIT_SUSPENDED_PKGS = "limit_suspended_pkgs"
    private const val KEY_BLOCKED_HIDDEN_PKGS = "blocked_hidden_pkgs"
    private const val KEY_KEYWORDS = "keywords"
    private const val KEY_REGEXES = "keyword_regexes"
    private const val KEY_BLOCKLIST_VERSION = "blocklist_version"

    private fun sp(ctx: Context): SharedPreferences =
        ctx.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    fun serverUrl(ctx: Context): String? = sp(ctx).getString(KEY_SERVER_URL, null)
    fun setServerUrl(ctx: Context, url: String) = sp(ctx).edit().putString(KEY_SERVER_URL, url).apply()

    fun deviceToken(ctx: Context): String? = sp(ctx).getString(KEY_DEVICE_TOKEN, null)
    fun deviceId(ctx: Context): Long = sp(ctx).getLong(KEY_DEVICE_ID, -1L)

    fun saveEnrollment(ctx: Context, deviceId: Long, token: String) {
        sp(ctx).edit()
            .putLong(KEY_DEVICE_ID, deviceId)
            .putString(KEY_DEVICE_TOKEN, token)
            .apply()
    }

    fun isEnrolled(ctx: Context): Boolean = deviceToken(ctx) != null

    fun parentPinHash(ctx: Context): String? = sp(ctx).getString(KEY_PARENT_PIN_HASH, null)
    fun setParentPinHash(ctx: Context, hash: String) = sp(ctx).edit().putString(KEY_PARENT_PIN_HASH, hash).apply()

    fun isLocked(ctx: Context): Boolean = sp(ctx).getBoolean(KEY_LOCKED, false)
    fun setLocked(ctx: Context, locked: Boolean) = sp(ctx).edit().putBoolean(KEY_LOCKED, locked).apply()

    /** Comma-separated list of phone numbers allowed to send SMS commands. */
    fun trustedNumbers(ctx: Context): List<String> =
        (sp(ctx).getString(KEY_TRUSTED_NUMBERS, "") ?: "").split(",").map { it.trim() }.filter { it.isNotEmpty() }
    fun setTrustedNumbers(ctx: Context, raw: String) = sp(ctx).edit().putString(KEY_TRUSTED_NUMBERS, raw).apply()

    /** Shared secret that must appear in an SMS command, since senders can be spoofed. */
    fun commandSecret(ctx: Context): String? = sp(ctx).getString(KEY_COMMAND_SECRET, null)
    fun setCommandSecret(ctx: Context, secret: String) = sp(ctx).edit().putString(KEY_COMMAND_SECRET, secret).apply()

    /** Lowercased keyword list mirrored from the server, so matching works offline. */
    fun keywords(ctx: Context): Set<String> =
        sp(ctx).getStringSet(KEY_KEYWORDS, emptySet()) ?: emptySet()
    /** Regex patterns, kept as written: lowercasing would turn \S into \s. */
    fun regexes(ctx: Context): Set<String> =
        sp(ctx).getStringSet(KEY_REGEXES, emptySet()) ?: emptySet()
    fun setKeywords(ctx: Context, words: List<String>, regexes: List<String>) {
        val clean = words.map { it.trim().lowercase() }.filter { it.isNotEmpty() }.toSet()
        sp(ctx).edit()
            .putStringSet(KEY_KEYWORDS, clean)
            .putStringSet(KEY_REGEXES, regexes.filter { it.isNotBlank() }.toSet())
            .apply()
    }

    /** Server version of the DNS blocklist on disk, so a change is re-downloaded and nothing else is. */
    fun blocklistVersion(ctx: Context): String? = sp(ctx).getString(KEY_BLOCKLIST_VERSION, null)
    fun setBlocklistVersion(ctx: Context, v: String) = sp(ctx).edit().putString(KEY_BLOCKLIST_VERSION, v).apply()

    /** Whether the current lock state was put in place by schedule enforcement, vs a manual/SMS LOCK. */
    fun isScheduleLockActive(ctx: Context): Boolean = sp(ctx).getBoolean(KEY_SCHEDULE_LOCK_ACTIVE, false)
    fun setScheduleLockActive(ctx: Context, active: Boolean) = sp(ctx).edit().putBoolean(KEY_SCHEDULE_LOCK_ACTIVE, active).apply()

    /** Packages AppRuleManager suspended for going over a daily limit, keyed by
     *  local date (yyyy-MM-dd) so a day rollover naturally forgets them. */
    fun limitSuspendedPackages(ctx: Context, day: String): Set<String> {
        val stored = sp(ctx).getString(KEY_LIMIT_SUSPENDED_DAY, null)
        if (stored != day) return emptySet()
        return sp(ctx).getStringSet(KEY_LIMIT_SUSPENDED_PKGS, emptySet()) ?: emptySet()
    }
    fun setLimitSuspendedPackages(ctx: Context, day: String, pkgs: Set<String>) {
        sp(ctx).edit()
            .putString(KEY_LIMIT_SUSPENDED_DAY, day)
            .putStringSet(KEY_LIMIT_SUSPENDED_PKGS, pkgs)
            .apply()
    }

    /** Packages hidden by a block rule, so one that is later unblocked gets unhidden. */
    fun blockedHiddenPackages(ctx: Context): Set<String> =
        sp(ctx).getStringSet(KEY_BLOCKED_HIDDEN_PKGS, emptySet()) ?: emptySet()
    fun setBlockedHiddenPackages(ctx: Context, pkgs: Set<String>) {
        sp(ctx).edit().putStringSet(KEY_BLOCKED_HIDDEN_PKGS, pkgs).apply()
    }
}
