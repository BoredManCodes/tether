package com.trentbuckley.tether.sync

import android.content.Context
import android.location.Location
import android.location.LocationManager
import android.os.CancellationSignal
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * Asks for a fresh fix, provider by provider, and falls back to the last
 * known one. Blocks, so call it off the main thread.
 */
object LocationFetcher {

    private val providers = listOf(LocationManager.FUSED_PROVIDER, LocationManager.GPS_PROVIDER, LocationManager.NETWORK_PROVIDER)

    fun fetch(ctx: Context, perProviderMs: Long = 4000): Location? {
        val lm = ctx.getSystemService(Context.LOCATION_SERVICE) as LocationManager
        for (provider in providers) {
            try {
                if (!lm.isProviderEnabled(provider)) continue
                val latch = CountDownLatch(1)
                var found: Location? = null
                val cancel = CancellationSignal()
                lm.getCurrentLocation(provider, cancel, { it.run() }) { loc ->
                    found = loc
                    latch.countDown()
                }
                if (!latch.await(perProviderMs, TimeUnit.MILLISECONDS)) cancel.cancel()
                found?.let { return it }
            } catch (_: SecurityException) {
                // Permission not granted for this provider; try the next.
            }
        }
        for (provider in providers) {
            try {
                lm.getLastKnownLocation(provider)?.let { return it }
            } catch (_: SecurityException) {
            }
        }
        return null
    }
}
