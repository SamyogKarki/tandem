package io.github.samyogkarki.tandem

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.widget.Button
import android.widget.TextView

/** A small status screen. Setup itself happens from the Tandem app on the PC. */
class MainActivity : Activity() {
    private val handler = Handler(Looper.getMainLooper())
    private val refresh = object : Runnable {
        override fun run() {
            render()
            handler.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        findViewById<Button>(R.id.test_button).setOnClickListener { TestNotifications.post(this) }
        findViewById<Button>(R.id.access_button).setOnClickListener {
            startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        }
    }

    override fun onResume() {
        super.onResume()
        handler.post(refresh)
    }

    override fun onPause() {
        handler.removeCallbacks(refresh)
        super.onPause()
    }

    private fun render() {
        val listening = NotificationBridgeService.instance != null
        val pc = PcLink.connected
        val paired = Pairing.isPaired(this)
        findViewById<TextView>(R.id.status_access).text =
            getString(if (listening) R.string.status_access_on else R.string.status_access_off)
        findViewById<TextView>(R.id.status_pc).text = getString(
            when {
                pc -> R.string.status_pc_connected
                paired -> R.string.status_pc_waiting
                else -> R.string.status_pc_not_paired
            }
        )
        findViewById<Button>(R.id.access_button).visibility = if (listening) Button.GONE else Button.VISIBLE
    }
}
