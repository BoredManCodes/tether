package com.trentbuckley.tether

import android.app.Application
import com.trentbuckley.tether.sync.TetherSyncService

class TetherApp : Application() {
    override fun onCreate() {
        super.onCreate()
        if (Prefs.isEnrolled(this)) {
            TetherSyncService.start(this)
        }
    }
}
