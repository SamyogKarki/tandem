package io.github.samyogkarki.tandem

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log

/**
 * A foreground service that runs only while the PC is connected (plus a short grace period).
 *
 * Why it exists: Xiaomi's HyperOS freezes idle background apps within seconds
 * (GreezeManager: "FZ uid = … reason = from system"). A frozen app can't answer its socket,
 * so the PC's replies, dismissals and reconnects stall. A foreground service keeps the process
 * out of the freezer. Android requires a notification for it, so it doubles as an honest
 * "Connected to your PC" indicator — silent, and gone shortly after the PC disconnects.
 */
class LinkService : Service() {
    companion object {
        private const val TAG = "TandemLinkService"
        private const val CHANNEL = "pc_link"
        private const val NOTIFICATION_ID = 2
        private const val GRACE_MS = 120_000L
        private val main = Handler(Looper.getMainLooper())
        private val stopRunnable = Runnable { instance?.stopSelf() }

        @Volatile
        private var instance: LinkService? = null

        /** Starts (or keeps) the service; cancels a pending stop. Safe to call often. */
        fun start(context: Context) {
            main.removeCallbacks(stopRunnable)
            try {
                context.startForegroundService(Intent(context, LinkService::class.java))
            } catch (e: Exception) {
                // e.g. ForegroundServiceStartNotAllowedException if the app lost its battery exemption.
                Log.w(TAG, "Couldn't start the foreground service: ${e.message}")
            }
        }

        /** The PC went away: keep running briefly (quick reconnects), then stop. */
        fun stopSoon() {
            main.removeCallbacks(stopRunnable)
            main.postDelayed(stopRunnable, GRACE_MS)
        }
    }

    override fun onCreate() {
        super.onCreate()
        instance = this
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL, getString(R.string.link_channel_name), NotificationManager.IMPORTANCE_LOW).apply {
                description = getString(R.string.link_channel_description)
                setShowBadge(false)
            }
        )
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE,
        )
        val notification = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_tandem)
            .setContentTitle(getString(R.string.link_notification_title))
            .setContentText(getString(R.string.link_notification_text))
            .setContentIntent(open)
            .setOngoing(true)
            .setShowWhen(false)
            .setCategory(Notification.CATEGORY_SERVICE)
            .setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE)
            .build()
        startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        if (PcLink.connected) main.removeCallbacks(stopRunnable) else stopSoon()
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        instance = null
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}

/**
 * The PC calls this (`adb shell am broadcast -n …/.WakeReceiver`) just before connecting.
 * Delivering a broadcast thaws a frozen app, and the receiver starts [LinkService] so it stays
 * thawed. Guarded by the DUMP permission: only the adb shell or the system can send it.
 */
class WakeReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        LinkService.start(context)
        resultCode = Activity.RESULT_OK
        resultData = "awake"
    }
}
